using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using Sentry.Extensibility;
using Sentry.Protocol;

namespace Sentry.Internal
{
    internal class MainExceptionProcessor : ISentryEventExceptionProcessor
    {
        private readonly SentryOptions _options;

        public MainExceptionProcessor(SentryOptions options)
            => _options = options ?? throw new ArgumentNullException(nameof(options));

        public void Process(Exception exception, SentryEvent sentryEvent)
        {
            Debug.Assert(sentryEvent != null);

            if (exception != null)
            {
                var sentryExceptions = CreateSentryException(exception)
                    // Otherwise realization happens on the worker thread before sending event.
                    .ToList();

                MoveExceptionExtrasToEvent(sentryEvent, sentryExceptions);

                sentryEvent.SentryExceptionValues = new SentryValues<SentryException>(sentryExceptions);
            }
        }

        // SentryException.Extra is not supported by Sentry yet.
        // Move the extras to the Event Extra while marking
        // by index the Exception which owns it
        private static void MoveExceptionExtrasToEvent(
            SentryEvent sentryEvent,
            IReadOnlyList<SentryException> sentryExceptions)
        {
            for (var i = 0; i < sentryExceptions.Count; i++)
            {
                var sentryException = sentryExceptions[i];

                if (!(sentryException.Data?.Count > 0))
                {
                    continue;
                }

                foreach (var key in sentryException.Data.Keys)
                {
                    sentryEvent.SetExtra($"Exception[{i}][{key}]", sentryException.Data[key]);
                }
            }
        }

        internal IEnumerable<SentryException> CreateSentryException(Exception exception)
        {
            Debug.Assert(exception != null);

            if (exception is AggregateException ae)
            {
                foreach (var inner in ae.InnerExceptions.SelectMany(CreateSentryException))
                {
                    yield return inner;
                }
            }
            else if (exception.InnerException != null)
            {
                foreach (var inner in CreateSentryException(exception.InnerException))
                {
                    yield return inner;
                }
            }

            var sentryEx = new SentryException
            {
                Type = exception.GetType()?.FullName,
                Module = exception.GetType()?.Assembly?.FullName,
                Value = exception.Message,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                Mechanism = GetMechanism(exception)
            };

            if (exception.Data.Count != 0)
            {
                foreach (var key in exception.Data.Keys)
                {
                    if (key is string keyString)
                    {
                        sentryEx.Data[keyString] = exception.Data[key];
                    }
                }
            }

            var stackTrace = new StackTrace(exception, true);

            // Sentry expects the frames to be sent in reversed order
            var frames = stackTrace.GetFrames()
                ?.Reverse()
                .Select(CreateSentryStackFrame);

            if (frames != null)
            {
                sentryEx.Stacktrace = new SentryStackTrace();
                foreach (var frame in frames)
                {
                    sentryEx.Stacktrace.Frames.Add(frame);
                }
            }

            yield return sentryEx;
        }

        internal static Mechanism GetMechanism(Exception exception)
        {
            Debug.Assert(exception != null);

            Mechanism mechanism = null;

            if (exception.HelpLink != null)
            {
                mechanism = new Mechanism
                {
                    HelpLink = exception.HelpLink
                };
            }

            return mechanism;
        }

        internal SentryStackFrame CreateSentryStackFrame(StackFrame stackFrame)
        {
            const string unknownRequiredField = "(unknown)";

            var frame = new SentryStackFrame();

            // stackFrame.HasMethod() throws NotImplemented on Mono 5.12
            if (stackFrame.GetMethod() is MethodBase method)
            {
                // TODO: SentryStackFrame.TryParse and skip frame instead of these unknown values:
                frame.Module = method.DeclaringType?.FullName ?? unknownRequiredField;
                frame.Package = method.DeclaringType?.Assembly.FullName;
                frame.Function = method.Name;
                frame.ContextLine = method.ToString();
            }

            frame.InApp = !IsSystemModuleName(frame.Module);
            frame.FileName = stackFrame.GetFileName();

            // stackFrame.HasILOffset() throws NotImplemented on Mono 5.12
            var ilOffset = stackFrame.GetILOffset();
            if (ilOffset != 0)
            {
                frame.InstructionOffset = stackFrame.GetILOffset();
            }

            var lineNo = stackFrame.GetFileLineNumber();
            if (lineNo != 0)
            {
                frame.LineNumber = lineNo;
            }

            var colNo = stackFrame.GetFileColumnNumber();
            if (lineNo != 0)
            {
                frame.ColumnNumber = colNo;
            }

            // TODO: Consider Ben.Demystifier
            DemangleAsyncFunctionName(frame);
            DemangleAnonymousFunction(frame);

            return frame;
        }

        private bool IsSystemModuleName(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName))
            {
                return false;
            }

            foreach (var exclude in _options.InAppExclude)
            {
                if (moduleName.StartsWith(exclude, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Clean up function and module names produced from `async` state machine calls.
        /// </summary>
        /// <para>
        /// When the Microsoft cs.exe compiler compiles some modern C# features,
        /// such as async/await calls, it can create synthetic function names that
        /// do not match the function names in the original source code. Here we
        /// reverse some of these transformations, so that the function and module
        /// names that appears in the Sentry UI will match the function and module
        /// names in the original source-code.
        /// </para>
        private static void DemangleAsyncFunctionName(SentryStackFrame frame)
        {
            if (frame.Module == null || frame.Function != "MoveNext")
            {
                return;
            }

            //  Search for the function name in angle brackets followed by d__<digits>.
            //
            // Change:
            //   RemotePrinterService+<UpdateNotification>d__24 in MoveNext at line 457:13
            // to:
            //   RemotePrinterService in UpdateNotification at line 457:13

            var match = Regex.Match(frame.Module, @"^(.*)\+<(\w*)>d__\d*$");
            if (match.Success && match.Groups.Count == 3)
            {
                frame.Module = match.Groups[1].Value;
                frame.Function = match.Groups[2].Value;
            }
        }

        /// <summary>
        /// Clean up function names for anonymous lambda calls.
        /// </summary>
        internal static void DemangleAnonymousFunction(SentryStackFrame frame)
        {
            if (frame?.Function == null)
            {
                return;
            }

            // Search for the function name in angle brackets followed by b__<digits/letters>.
            //
            // Change:
            //   <BeginInvokeAsynchronousActionMethod>b__36
            // to:
            //   BeginInvokeAsynchronousActionMethod { <lambda> }

            var match = Regex.Match(frame.Function, @"^<(\w*)>b__\w+$");
            if (match.Success && match.Groups.Count == 2)
            {
                frame.Function = match.Groups[1].Value + " { <lambda> }";
            }
        }
    }
}
