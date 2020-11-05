using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using Optional;
using Optional.Linq;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class ManifestService
    {
        private readonly Uri url;
        private readonly ILogger logger;

        private const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/85.0.4183.83 Safari/537.36 Edg/85.0.564.44";
        private static readonly HttpClient http = CreateHttpClient();

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
            var manifestNode = LoadManifestNode(document);
            var manifestUrl = GetManifestUrl(manifestNode);
            var manifestContents = await LoadManifest(manifestUrl);
            var manifestObject = DeserializeManifest(manifestContents);
            return new ManifestResult
            {
                ManifestUrl = manifestUrl,
                ManifestContents = manifestObject
            };
        }

        private HtmlNode LoadManifestNode(HtmlDocument document)
        {
            var node = document.DocumentNode?.SelectSingleNode("//head/link[@rel='manifest']");
            if (node == null)
            {
                var error = new Exception("Unable to find manifest node in document");
                var headNode = document.DocumentNode?.SelectSingleNode("//head");
                if (headNode != null)
                {
                    error.Data.Add("headNode", headNode.OuterHtml);
                }
                throw error;
            }

            return node;
        }

        private async Task<string> LoadManifest(Uri manifestUrl)
        {
            try
            {
                var manifestContents = await FetchHttpWithHttp2Fallback(manifestUrl, "application/json");
                if (string.IsNullOrWhiteSpace(manifestContents))
                {
                    throw new Exception($"Fetched manifest from {manifestUrl}, but the contents was empty");
                }

                return manifestContents;
            }
            catch (Exception manifestFetchError)
            {
                manifestFetchError.Data.Add("manifestUrl", manifestUrl);
                throw;
            }
        }

        private async Task<string?> FetchHttpWithHttp2Fallback(Uri url, string? acceptHeader)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(acceptHeader))
                {
                    httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(acceptHeader));
                }
                var httpResponse = await http.SendAsync(httpRequest);
                httpResponse.EnsureSuccessStatusCode();
                return await httpResponse.Content.ReadAsStringAsync();
            }
            catch (Exception httpException)
            {
                logger.LogWarning(httpException, "Failed to load {url} using HTTP. Falling back to HTTP/2.", url);
            }

            // Attempt HTTP/2
            try
            {
                using var http2Request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = new Version(2, 0)
                };
                if (!string.IsNullOrEmpty(acceptHeader))
                {
                    http2Request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(acceptHeader));
                }
                using var result = await http.SendAsync(http2Request);
                result.EnsureSuccessStatusCode();
                var contentString = await result.Content.ReadAsStringAsync();
                logger.LogInformation("Successfully fetched {url} via HTTP/2 fallback", url);
                return contentString;
            }
            catch (Exception http2Error)
            {
                logger.LogWarning(http2Error, "Unable to fetch {url} using HTTP/2 fallback.", url);
                return null;
            }
        }

        private object DeserializeManifest(string manifestContents)
        {
            // Try to parse it into an object. Failure to do this suggests malformed manifest JSON.
            // We've also seen issues where sites are misconfigured to return the HTML of the page when requesting the manifest.
            try
            {
                return JsonConvert.DeserializeObject<dynamic>(manifestContents);
            }
            catch (Exception deserializeError)
            {
                deserializeError.Data.Add("manifestJson", manifestContents);
                logger.LogError(deserializeError, "Fetched manifest contents but was unable to deserialize it into an object. Raw JSON: \r\n\r\n{json}", manifestContents);
                throw;
            }
        }

        private Uri GetManifestUrl(HtmlNode manifestNode)
        {
            var manifestHref = manifestNode.Attributes["href"]?.Value;
            if (string.IsNullOrWhiteSpace(manifestHref))
            {
                throw new Exception($"Manifest element was found, but href was missing. Raw HTML was {manifestNode.OuterHtml}");
            }

            // If the site URL has a local path (e.g. "/gridscore" in the URL https://ics.hutton.ac.uk/gridscore), then
            // we cannot just do Uri.TryCreate(this.url, manifestHref), because this will omit the local path.
            // To fix this, we append the "/" to the absolute path.
            // Broke: new Uri(new Uri("https://ics.hutton.ac.uk/gridscore"), "site.webmanifest") => "https://ics.hutton.ac.uk/site.webmanifest" (wrong manifest URL!)
            // Fixed: new Uri(new Uri("https://ics.hutton.ac.uk/gridscore/"), "site.webmanifest") => "https://ics.hutton.ac.uk/gridscore/site.webmanifest" (correct manifest URL)
            var rootUrl = string.IsNullOrEmpty(this.url.PathAndQuery) || this.url.PathAndQuery == "/" ? url : new Uri(this.url.AbsoluteUri.TrimEnd('/') + "/");
            if (!Uri.TryCreate(rootUrl, manifestHref, out var manifestUrl))
            {
                var manifestHrefInvalid = new Exception($"Manifest element was found, but couldn't construct an absolute URI from '{this.url}' and '{manifestHref}'");
                manifestHrefInvalid.Data.Add("manifestHref", manifestHref);
                manifestHrefInvalid.Data.Add("manifestNodeHtml", manifestNode.OuterHtml);
                manifestHrefInvalid.Data.Add("headHtml", manifestNode.ParentNode?.InnerHtml);
                throw manifestHrefInvalid;
            }

            return manifestUrl;
        }

        private async Task<HtmlDocument> LoadPage()
        {
            var web = new HtmlWeb
            {
                UserAgent = userAgent
            };
            try
            {
                return await web.LoadFromWebAsync(this.url, null, null);
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Unable to load {url} via HtmlAgilityPack. Falling back to HttpClient load.", url);
                var manuallyLoadedHtmlDoc = await TryLoadPageViaHttpClient();
                if (manuallyLoadedHtmlDoc != null)
                {
                    logger.LogInformation("Fallback successful, loaded {url} via HttpClient.", url);
                    return manuallyLoadedHtmlDoc;
                }
                else
                {
                    logger.LogWarning("Fallback also failed to loaded {url}", url);
                }
                
                throw new Exception("Unable to download page for " + url.ToString(), error);
            }
        }

        private async Task<HtmlDocument?> TryLoadPageViaHttpClient()
        {
            string? html;
            try
            {
                html = await FetchHttpWithHttp2Fallback(this.url, "text/html");
            }
            catch (Exception httpError)
            {
                logger.LogError(httpError, "Fallback to fetch {url} with HttpClient failed.", this.url);
                return null;
            }

            try
            {
                var htmlDoc = new HtmlDocument();
                htmlDoc.LoadHtml(html ?? string.Empty);
                return htmlDoc;
            }
            catch (Exception docLoadError)
            {
                logger.LogError(docLoadError, "Fetched page via HttpClient fallback, but the HTML couldn't be loaded into a document. Raw HTML: \r\n\r\n{raw}", html);
                return null;
            }
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient(new HttpClientHandler
            {
                // Don't worry about HTTPS errors
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                AllowAutoRedirect = true
            });
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            return http;
        }
    }
}
