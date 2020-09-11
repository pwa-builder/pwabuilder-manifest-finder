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

namespace Microsoft.PWABuilder.ManifestFinder
{
    public static class Function
    {
        [FunctionName("FindManifest")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            // Grab the required URL
            var url = req.Query["url"].FirstOrDefault();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                log.LogError("No valid url was specified. URL query text = '{rawUrl}'", url);
                return new BadRequestObjectResult("You must specify a URL in the query string");
            }

            // Grab the optional verbose flag
            var verbose = req.Query["verbose"].FirstOrDefault() == "1";

            log.LogInformation("Running manifest detection for {url}", uri);

            var manifestService = new ManifestService(uri, log);
            ManifestResult result;
            try
            {
                result = await manifestService.Run();
            }
            catch (Exception manifestLoadError)
            {
                var errorMessage = verbose ? manifestLoadError.ToDetailedString() : manifestLoadError.Message;
                log.LogWarning(manifestLoadError, "Failed to detect manifest for {url}. {message}", url, errorMessage);
                result = new ManifestResult
                {
                    Error = errorMessage
                };
            }

            if (result.ManifestContents != null)
            {
                log.LogInformation("Successfully detected manifest for {url}", url);
            }
            return new OkObjectResult(result);
        }
    }
}
