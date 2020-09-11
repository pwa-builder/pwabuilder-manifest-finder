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
            var url = req.Query["url"].FirstOrDefault();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                log.LogError("No valid url was specified. URL query text = '{rawUrl}'", url);
                return new BadRequestObjectResult("You must specify a URL in the query string");
            }

            log.LogInformation("Running manifest detection for {url}", uri);

            var manifestService = new ManifestService(uri, log);
            ManifestResult result;
            try
            {
                result = await manifestService.Run();
            }
            catch (Exception manifestLoadError)
            {
                log.LogError(manifestLoadError, "Unable to detect manifest");
                result = new ManifestResult
                {
                    Error = "Error during manifest detection. " + manifestLoadError.ToString()
                };
            }

            return new OkObjectResult(result);
        }
    }
}
