﻿using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class Analytics
    {
        private readonly AppSettings settings;
        private readonly ILogger logger;
        private static readonly HttpClient http = new HttpClient();

        public Analytics(
            AppSettings settings, 
            ILogger logger)
        {
            this.settings = settings;
            this.logger = logger;
        }

        public void RecordManifestDetectionResults(Uri url, bool manifestDetected, int? manifestScore, string? manifestMissingDetails, string? error, TimeSpan elapsed)
        {
            try
            {
                LogUrlResultCore(url, manifestDetected, manifestScore, manifestMissingDetails, error, elapsed);
            }
            catch (Exception urlLogError)
            {
                logger.LogWarning(urlLogError, "Unable to log URL due to exception");
                // We don't throw the exception here, as we don't consider it catastrophic.
            }
        }

        private void LogUrlResultCore(Uri url, bool manifestDetected, int? manifestScore, string? manifestMissingDetails, string? error, TimeSpan elapsed)
        {
            if (string.IsNullOrEmpty(this.settings.UrlLoggingApi))
            {
                this.logger.LogWarning("Skipping URL recording due to no configured url log service API");
                return;
            }

            var args = JsonSerializer.Serialize(new
            {
                Url = url,
                ManifestDetected = manifestDetected,
                ManifestMissingDetails = manifestMissingDetails,
                ManifestDetectionError = error,
                ManifestDetectionTimeInMs = elapsed.TotalMilliseconds,
                ManifestScore = manifestScore
            });
            http.PostAsync(this.settings.UrlLoggingApi, new StringContent(args))
                .ContinueWith(_ => logger.LogInformation("Successfully sent {url} to URL logging service. Success = {success}, Error = {error}, Elapsed = {elapsed}", url, manifestDetected, error, elapsed), TaskContinuationOptions.OnlyOnRanToCompletion)
                .ContinueWith(task => logger.LogWarning(task.Exception ?? new Exception("Unable to send URL to logging service"), "Unable to send {url} to logging service due to an error", url), TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
