using System;
using System.ComponentModel;
using Sentry.Extensions.Logging;

// ReSharper disable once CheckNamespace
// Ensures 'AddSentry' can be found without: 'using Sentry;'
namespace Microsoft.Extensions.Logging
{
    ///
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class SentryLoggerFactoryExtensions
    {
        /// <summary>
        /// Adds the Sentry logging integration.
        /// </summary>
        /// <remarks>
        /// This method does not need to be called when calling `UseSentry` with ASP.NET Core
        /// since that integrates with the logging framework automatically.
        /// </remarks>
        /// <param name="factory">The factory.</param>
        /// <param name="optionsConfiguration">The options configuration.</param>
        /// <returns></returns>
        public static ILoggerFactory AddSentry(
            this ILoggerFactory factory,
            Action<SentryLoggingOptions> optionsConfiguration = null)
        {
            var options = new SentryLoggingOptions();
            optionsConfiguration?.Invoke(options);

            factory.AddProvider(new SentryLoggerProvider(options));
            return factory;
        }
    }
}
