using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class ManifestService
    {
        private readonly Uri url;
        private readonly ILogger logger;

        private static readonly HttpClient http = new HttpClient(new HttpClientHandler
        {
            // Don't worry about HTTPS errors
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AllowAutoRedirect = true
        });

        public ManifestService(Uri url, ILogger logger)
        {
            this.url = url;
            this.logger = logger;
        }

        /// <summary>
        /// Downloads the web page, searches for the manifest, then downloads the contents of the manifest.
        /// </summary>
        /// <returns></returns>
        public async Task<ManifestResult> Run()
        {
            var document = await LoadPage();
            var manifestNode = document.DocumentNode?.SelectSingleNode("//head/link[@rel='manifest']");
            if (manifestNode == null)
            {
                logger.LogInformation("Unable to locate manifest link tag inside <head> element");
                return new ManifestResult
                {
                    Error = "Unable to locate manifest link tag inside head"
                };
            }

            // Try to get the absolute URL to the manifest.
            var manifestUrl = GetManifestUrl(manifestNode);
            if (manifestUrl == null)
            {
                return new ManifestResult
                {
                    Error = "Manifest link element was found, but href couldn't be parsed into a valid URI. Node HTML was " + manifestNode.OuterHtml
                };
            }

            // Load the contents of the manifest.
            var manifestContents = await LoadManifest(manifestUrl);
            if (manifestContents == null)
            {
                return new ManifestResult
                {
                    Error = $"Manifest URL was found, but downloading manifest from {url} resulted in a null or empty string",
                    ManifestUrl = url,
                    ManifestContents = null
                };
            }

            return new ManifestResult
            {
                ManifestUrl = manifestUrl,
                ManifestContents = manifestContents
            };
        }

        private async Task<string?> LoadManifest(Uri manifestUrl)
        {
            try
            {
                var manifestContents = await http.GetStringAsync(manifestUrl);
                if (string.IsNullOrWhiteSpace(manifestContents))
                {
                    logger.LogInformation("Fetched manifest from {url}, but the contents was empty", manifestUrl);
                    return null;
                }

                return manifestContents;
            }
            catch (Exception manifestFetchError)
            {
                throw new Exception("Error fetching manifest contents from " + manifestUrl.ToString(), manifestFetchError);
            }
        }

        private Uri? GetManifestUrl(HtmlNode manifestNode)
        {
            var manifestHref = manifestNode.Attributes["href"]?.Value;
            if (!Uri.TryCreate(this.url, manifestHref, out var manifestUrl))
            {
                logger.LogInformation("Manifest element was found, but href was invalid. Couldn't construct an absolute URI from {baseUrl} and {relativeUrl}. HTML of manifest link node = {nodeHtml}. Entire Head contents:\r\n\r\n{head}", this.url, manifestHref, manifestNode.OuterHtml, manifestNode.ParentNode?.InnerHtml);
                return null;
            }

            return manifestUrl;
        }

        private async Task<HtmlDocument> LoadPage()
        {
            var web = new HtmlWeb
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/85.0.4183.83 Safari/537.36 Edg/85.0.564.44",
            };
            try
            {
                var document = await web.LoadFromWebAsync(this.url, null, null);
                return document;
            }
            catch (Exception error)
            {
                var manuallyLoadedHtmlDoc = await TryLoadPageViaHttpClient();
                if (manuallyLoadedHtmlDoc != null)
                {
                    return manuallyLoadedHtmlDoc;
                }
                
                throw new Exception("Unable to download page for " + url.ToString(), error);
            }
        }

        private async Task<HtmlDocument?> TryLoadPageViaHttpClient()
        {
            string html;
            try
            {
                html = await http.GetStringAsync(this.url);
            }
            catch (Exception httpError)
            {
                logger.LogError(httpError, "Fallback to fetch page with HttpClient failed");
                return null;
            }

            try
            {
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html);
                return htmlDoc;
            }
            catch (Exception docLoadError)
            {
                logger.LogError(docLoadError, "Fetched page via HttpClient fallback, but the HTML couldn't be loaded into a document. Raw HTML: \r\n\r\n{raw}", html);
                return null;
            }
        }
    }
}
