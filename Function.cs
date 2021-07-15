using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Linq;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public static class Function
    {
        [FunctionName("FindManifest")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log,
            ExecutionContext context)
        {
            var appSettings = LoadSettings(context, log);

            // Grab the required URL
            var url = req.Query["url"].FirstOrDefault();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                log.LogError("No valid url was specified. URL query text = '{rawUrl}'", url);
                return new BadRequestObjectResult("You must specify a URL in the query string");
            }
            if (uri.IsLoopback)
            {
                return new BadRequestObjectResult("URIs must not be local");
            }

            // Grab the optional verbose flag
            var verbose = req.Query["verbose"].FirstOrDefault() == "1";

            log.LogInformation("Running manifest detection for {url}", uri);

            var manifestService = new ManifestService(uri, log);
            var urlLogger = new Analytics(appSettings, log);
            ManifestResult result;
            var stopwatch = new Stopwatch();
            stopwatch.Start();
            try
            {
                result = await manifestService.Run();
                urlLogger.RecordManifestDetectionResults(uri, result.Error == null, result.ManifestScore?.Sum(kv => kv.Value), null, result.Error, stopwatch.Elapsed);
            }
            catch (Exception manifestLoadError)
            {
                var errorMessage = verbose ? manifestLoadError.ToDetailedString() : manifestLoadError.GetMessageWithInnerMessages();
                log.LogWarning(manifestLoadError, "Failed to detect manifest for {url}. {message}", url, errorMessage);
                result = new ManifestResult 
                { 
                    Error = errorMessage,
                    ManifestContainsInvalidJson = manifestLoadError is ManifestContainsInvalidJsonException
                };
                var manifestMissingDetails = manifestLoadError is ManifestNotFoundException ? manifestLoadError.Message : null;
                var unexpectedError = manifestMissingDetails == null ? errorMessage : null;
                urlLogger.RecordManifestDetectionResults(uri, false, null, manifestMissingDetails, unexpectedError, stopwatch.Elapsed);
            }
            finally
            {
                stopwatch.Stop();
            }

            if (result.ManifestContents != null)
            {
                log.LogInformation("Successfully detected manifest for {url}", url);
            }
            return new OkObjectResult(result);
        }

        private static AppSettings LoadSettings(ExecutionContext context, ILogger log)
        {
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(context.FunctionAppDirectory)
                    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: false)
                    .AddEnvironmentVariables()
                    .Build();
                var settings = new AppSettings();
                config.GetSection("AppSettings").Bind(settings);
                return settings;
            }
            catch (Exception settingsError)
            {
                log.LogError(settingsError, "Exception occurred loading function settings");
                throw;
            }
        }
    }
}
