using System;
using System.Net;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Sentry.Samples.AspNetCore.Mvc
{
    public class Program
    {
        public static void Main(string[] args)
        {
            BuildWebHost(args).Run();
        }

        public static IWebHost BuildWebHost(string[] args) =>
            WebHost.CreateDefaultBuilder(args)
                .UseShutdownTimeout(TimeSpan.FromSeconds(10))
                .UseStartup<Startup>()

                // Example integration with advanced configuration scenarios:
                .UseSentry(options =>
                {
                    // The parameter 'options' here has values populated through the configuration system.
                    // That includes 'appsettings.json', environment variables and anything else
                    // defined on the ConfigurationBuilder.
                    // See: https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-2.1&tabs=basicconfiguration
                    options.Init(i =>
                    {
                        // Tracks the release which sent the event and enables more features: https://docs.sentry.io/learn/releases/
                        // If not explicitly set here, the SDK attempts to read it from: AssemblyInformationalVersionAttribute and AssemblyVersion
                        // TeamCity: %build.vcs.number%, VSTS: BUILD_SOURCEVERSION, Travis-CI: TRAVIS_COMMIT, AppVeyor: APPVEYOR_REPO_COMMIT, CircleCI: CIRCLE_SHA1
                        i.Release = "e386dfd"; // Could be also the be like: 2.0 or however your version your app

                        i.MaxBreadcrumbs = 200;

                        i.Http(h =>
                        {
                            //h.Proxy = new WebProxy("https://localhost:3128");

                            // Example: Disabling support to compressed responses:
                            h.DecompressionMethods = DecompressionMethods.None;
                        });

                        i.Worker(w =>
                        {
                            w.MaxQueueItems = 100;
                            w.ShutdownTimeout = TimeSpan.FromSeconds(5);
                        });
                    });

                    // Hard-coding here will override any value set on appsettings.json:
                    options.Logging.MinimumEventLevel = LogLevel.Error;
                })
                .Build();
    }
}
