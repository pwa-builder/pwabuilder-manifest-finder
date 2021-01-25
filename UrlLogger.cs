using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class UrlLogger
    {
        private readonly AppSettings settings;
        private readonly ILogger logger;
        private static readonly HttpClient http = new HttpClient();

        public UrlLogger(
            AppSettings settings, 
            ILogger logger)
        {
            this.settings = settings;
            this.logger = logger;
        }

        public void LogUrlResult(Uri url, bool success, string? error, TimeSpan elapsed)
        {
            if (string.IsNullOrEmpty(this.settings.UrlLoggingApi))
            {
                this.logger.LogWarning("Skipping URL recording due to no configured url log service API");
                return;
            }

            var args = System.Text.Json.JsonSerializer.Serialize(new
            {
                Url = url,
                ManifestDetected = success,
                ManifestDetectionError = error,
                ManifestDetectionTimeInMs = elapsed.TotalMilliseconds
            });
            http.PostAsync(this.settings.UrlLoggingApi, new StringContent(args))
                .ContinueWith(_ => logger.LogInformation("Successfully sent {url} to URL logging service. Success = {success}, Error = {error}, Elapsed = {elapsed}", url, success, error, elapsed), TaskContinuationOptions.OnlyOnRanToCompletion)
                .ContinueWith(task => logger.LogWarning(task.Exception ?? new Exception("Unable to send URL to logging service"), "Unable to send {url} to logging service due to an error", url), TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
