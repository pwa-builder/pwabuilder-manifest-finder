using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using Optional;
using Optional.Linq;
using System.Text.Json;
using System.Linq;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class ManifestService
    {
        private readonly Uri url;
        private readonly ILogger logger;

        private const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.212 Safari/537.36";
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
            var manifestContext = await LoadManifestInfo(manifestNode);            
            var (manifestObject, dynamicManifest) = DeserializeManifest(manifestContext.Json);
            var manifestScore = GetManifestScore(manifestObject);
            return new ManifestResult
            {
                ManifestUrl = manifestContext.Uri,
                ManifestContents = dynamicManifest, // Dynamic manifest here, otherwise we end up with null values for things that should be undefined, which throws some of our tooling (e.g. web package generator) for a loop.
                ManifestScore = manifestScore
            };
        }

        private HtmlNode LoadManifestNode(HtmlDocument document)
        {
            var node = document.DocumentNode?.SelectSingleNode("//head/link[@rel='manifest']") ??
                document.DocumentNode?.SelectSingleNode("//link[@rel='manifest']"); // We've witnesses some sites in the wild with no <head>, and they put the manifest link right in the HTML.
            if (node == null)
            {
                var error = new ManifestNotFoundException("Unable to find manifest node in document");
                var headNode = document.DocumentNode?.SelectSingleNode("//head");
                if (headNode != null)
                {
                    error.Data.Add("headNode", headNode.OuterHtml);
                }
                throw error;
            }

            return node;
        }

        private async Task<string?> TryFetchHttpWithHttp2Fallback(Uri url, string? acceptHeader)
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

        private (WebAppManifest parsedManifest, dynamic rawManifest) DeserializeManifest(string manifestContents)
        {
            // Try to parse it into an object. Failure to do this suggests malformed manifest JSON.
            // We've also seen issues where sites are misconfigured to return the HTML of the page when requesting the manifest.
            try
            {
                var parsedManifest = JsonSerializer.Deserialize<WebAppManifest>(manifestContents);
                dynamic dynamicManifest = Newtonsoft.Json.Linq.JObject.Parse(manifestContents);
                return (parsedManifest, dynamicManifest);
            }
            catch (Exception deserializeError)
            {
                deserializeError.Data.Add("manifestJson", manifestContents);
                logger.LogError(deserializeError, "Fetched manifest contents but was unable to deserialize it into an object. Raw JSON: \r\n\r\n{json}", manifestContents);
                throw;
            }
        }

        private Dictionary<string, int> GetManifestScore(WebAppManifest manifest)
        {
            var requiredFields = new[]
            {
                ("icons", 5, manifest.Icons?.Count > 0),
                ("name", 5, !string.IsNullOrWhiteSpace(manifest.Name)),
                ("short_name", 5, !string.IsNullOrWhiteSpace(manifest.ShortName)),
                ("start_url", 5, !string.IsNullOrWhiteSpace(manifest.StartUrl))
            };
            var recommendedFields = new[]
            {
                ("display", 2, !string.IsNullOrWhiteSpace(manifest.Display) && WebAppManifest.DisplayTypes.Contains(manifest.Display)),
                ("background_color", 2, !string.IsNullOrWhiteSpace(manifest.BackgroundColor)),
                ("description", 2, !string.IsNullOrWhiteSpace(manifest.Description)),
                ("orientation", 2, !string.IsNullOrWhiteSpace(manifest.Orientation) && WebAppManifest.OrientationTypes.Contains(manifest.Orientation)),
                ("screenshots", 2, manifest.Screenshots?.Count > 0),
                ("large_square_png_icon", 2, manifest.Icons?.Any(i => i.IsAnyPurpose() && i.IsPng() && i.IsSquare() && i.GetLargestDimension()?.height >= 512) == true),
                ("maskable_icon", 2, manifest.Icons?.Any(i => i.GetPurposes().Contains("maskable", StringComparer.InvariantCultureIgnoreCase)) == true),
                ("categories", 2, manifest.Categories?.Count > 0),
                ("shortcuts", 2, manifest.Shortcuts?.Count > 0)
            };
            var optionalFields = new[]
            {
                ("iarc_rating_id", 1, !string.IsNullOrWhiteSpace(manifest.IarcRatingId)),
                ("related_applications", 1, manifest.PreferRelatedApplications.HasValue && manifest.RelatedApplications != null)
            };

            return new Dictionary<string, int>(requiredFields
                .Concat(recommendedFields)
                .Concat(optionalFields)
                .Concat(new[] { ("manifest", 10, true) }) // 10 points for having a manifest
                .Select(a => new KeyValuePair<string, int>(a.Item1, a.Item3 ? a.Item2 : 0)));
        }

        private Task<ManifestContext> LoadManifestInfo(HtmlNode manifestNode)
        {
            // Make sure we have a valid href attribute on the manifest node.
            var manifestHref = manifestNode.Attributes["href"]?.Value;
            if (string.IsNullOrWhiteSpace(manifestHref))
            {
                throw new ManifestNotFoundException($"Manifest element was found, but href was missing. Raw HTML was {manifestNode.OuterHtml}");
            }

            logger.LogInformation("Manifest node detected with href {href}", manifestHref);

            return LoadManifestInfo(manifestHref, manifestNode);
        }

        private async Task<ManifestContext> LoadManifestInfo(string manifestHref, HtmlNode? manifestNode)
        {
            // First, try Uri.TryCreate(this.url, manifestHref) and see if it's valid.
            // This will work for most sites, e.g. https://www.sensoryapphouse.com/abstract4-pwa-xbox/index.html -> https://www.sensoryapphouse.com/abstract4-pwa-xbox/manifest.json
            if (Uri.TryCreate(this.url, manifestHref, out var manifestAbsoluteUrl))
            {
                // Fetch the manifest.
                logger.LogInformation("Attempting manifest download using absolute URL {url}", manifestAbsoluteUrl);
                var manifestContents = await TryFetchHttpWithHttp2Fallback(manifestAbsoluteUrl, "application/json");
                if (!string.IsNullOrEmpty(manifestContents))
                {
                    return new ManifestContext(manifestAbsoluteUrl, manifestContents);
                }

                logger.LogWarning("Unable to download manifest using absolute URL {url}. Falling back to local path detection.", manifestAbsoluteUrl);
            }

            // Fetching the manifest relative to the URL failed. This might mean the site has a local path that needs to end in slash.
            // If the site URL has a local path (e.g. "/gridscore" in the URL https://ics.hutton.ac.uk/gridscore), then
            // we cannot just do Uri.TryCreate(this.url, manifestHref), because this will omit the local path.
            // To fix this, we append the "/" to the absolute path:
            // Broke: new Uri(new Uri("https://ics.hutton.ac.uk/gridscore"), "site.webmanifest") => "https://ics.hutton.ac.uk/site.webmanifest" (wrong manifest URL!)
            // Fixed: new Uri(new Uri("https://ics.hutton.ac.uk/gridscore/"), "site.webmanifest") => "https://ics.hutton.ac.uk/gridscore/site.webmanifest" (correct manifest URL)
            Uri? localPathManifestUrl = null;
            if (!string.IsNullOrEmpty(url.PathAndQuery) && url.PathAndQuery != "/" && !url.AbsoluteUri.EndsWith("/"))
            {
                var rootUrl = new Uri(this.url.AbsoluteUri + "/");
                if (!Uri.TryCreate(rootUrl, manifestHref, out localPathManifestUrl))
                {
                    var manifestHrefInvalid = new ManifestNotFoundException($"Manifest element was found, but couldn't construct an absolute URI from '{this.url}' and '{manifestHref}'");
                    manifestHrefInvalid.Data.Add("manifestHref", manifestHref);
                    manifestHrefInvalid.Data.Add("manifestNodeHtml", manifestNode?.OuterHtml);
                    manifestHrefInvalid.Data.Add("headHtml", manifestNode?.ParentNode?.InnerHtml);
                    throw manifestHrefInvalid;
                }

                logger.LogInformation("PWA URL has local path {path}. Attempting manifest detection with local path fallback and absolute manifest URL {url}.", url.PathAndQuery, localPathManifestUrl);
                var manifestContents = await TryFetchHttpWithHttp2Fallback(localPathManifestUrl, "application/json");
                if (!string.IsNullOrEmpty(manifestContents))
                {
                    return new ManifestContext(localPathManifestUrl, manifestContents);
                }
            }

            throw new ManifestNotFoundException($"Unable to detect manifest. Attempted manifest download at {manifestAbsoluteUrl} and {localPathManifestUrl}, but both failed.");
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
                html = await TryFetchHttpWithHttp2Fallback(this.url, "text/html");
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
            var http = new HttpClientIgnoringSslErrors();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
            return http;
        }
    }
}
