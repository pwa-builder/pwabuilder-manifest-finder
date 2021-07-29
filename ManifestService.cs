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

        // NOTE: user agent should include curl/7.64.1, otherwise some sites like Facebook Workplace will block us
        private const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36 Edg/91.0.864.59 curl/7.64.1";
        private static readonly HttpClient http = CreateHttpClient();
        private static readonly string[] manifestMimeTypes = new[] { "application/json", "application/manifest+json" };

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
            var document = await LoadPage(this.url);
            var manifestNode = await LoadManifestNode(document);
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

        private async Task<HtmlNode> LoadManifestNode(HtmlDocument document)
        {
            var manifestNode = document.DocumentNode?.SelectSingleNode("//head/link[@rel='manifest']") ??
                document.DocumentNode?.SelectSingleNode("//link[@rel='manifest']"); // We've witnesses some sites in the wild with no <head>, and they put the manifest link right in the HTML.

            // If we can't find a manifest node, see if we're being redirected via a <meta http-equiv="refresh" content="0; url='https://someotherurl'" /> tag
            // See https://github.com/pwa-builder/CloudAPK/issues/78#issuecomment-872132508
            if (manifestNode == null)
            {
                manifestNode = await TryLoadManifestNodeFromRedirectTag(document);
            }

            if (manifestNode == null)
            {
                var error = new ManifestNotFoundException("Unable to find manifest node in document");
                var headNode = document.DocumentNode?.SelectSingleNode("//head");
                if (headNode != null)
                {
                    error.Data.Add("headNode", headNode.OuterHtml);
                }
                throw error;
            }

            return manifestNode;
        }

        private async Task<HtmlNode?> TryLoadManifestNodeFromRedirectTag(HtmlDocument document)
        {
            // Redirect tags look like <meta http-equiv="refresh" content="0; url='https://someotherurl'" />

            // Do we have a redirect? If so, follow that and then see if we can load the manifest node.
            var redirectTag = document.DocumentNode?.SelectSingleNode("//head/meta[@http-equiv='refresh']");
            if (redirectTag != null)
            {
                var redirectSettings = redirectTag.Attributes["content"]?.Value ?? string.Empty;
                var redirectRegex = "url\\s*=\\s*['|\"]*([^'\"]+)";
                var regexMatch = System.Text.RegularExpressions.Regex.Match(redirectSettings, redirectRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (regexMatch.Success && regexMatch.Groups.Count == 2)
                {
                    var redirectUrl = regexMatch.Groups[1].Value;
                    
                    // Make sure it's a legit URI, and make sure it's not the page we're already on.
                    if (Uri.TryCreate(this.url, redirectUrl, out var redirectUri) && redirectUri != this.url)
                    {
                        logger.LogInformation("Page contained redirect tag in <head>. Redirecting to {url}", redirectUrl);
                        var redirectDoc = await LoadPage(redirectUri);
                        return await LoadManifestNode(redirectDoc);
                    }
                }
            }

            return null;
        }

        private async Task<string?> TryFetchWithHttpClient(Uri url, params string[] acceptHeaders)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                if (acceptHeaders != null)
                {
                    foreach (var header in acceptHeaders)
                    {
                        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(header));
                    }
                }

                var httpResponse = await http.SendAsync(httpRequest);
                httpResponse.EnsureSuccessStatusCode();
                return await httpResponse.Content.ReadAsStringAsync();
            }
            catch (InvalidOperationException invalidOpError) when (invalidOpError.Message.Contains("The character set provided in ContentType is invalid."))
            {
                // Invalid encoding? Sometimes webpages have incorrectly set their charset / content type.
                // See if we can just parse the thing using UTF-8.
                logger.LogWarning(invalidOpError, "Unable to parse using HTTP client due to invalid ContentType. Attempting to parse using UTF-8.");
                return await TryFetchWithForcedUtf8(url, acceptHeaders);
            }
            catch (Exception httpException)
            {
                logger.LogWarning(httpException, "Failed to fetch {url} using HTTP client. Falling back to HTTP/2 fetch.", url);
                return await TryFetchWithHttp2Client(url, acceptHeaders);
            }
        }

        private async Task<string?> TryFetchWithForcedUtf8(Uri url, params string[] acceptHeaders)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                if (acceptHeaders != null)
                {
                    foreach (var header in acceptHeaders)
                    {
                        httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(header));
                    }
                }

                var httpResponse = await http.SendAsync(httpRequest);
                httpResponse.EnsureSuccessStatusCode();
                var contentBytes = await httpResponse.Content.ReadAsByteArrayAsync();
                var responseString = Encoding.UTF8.GetString(contentBytes);
                logger.LogInformation("Successfully parsed the HTML using forced UTF-8 mode");
                return responseString;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Unable to parse HTML using forced UTF-8 mode.");
                return null;
            }
        }

        private async Task<string?> TryFetchWithHttp2Client(Uri url, params string[] acceptHeaders)
        {
            try
            {
                using var http2Request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = new Version(2, 0)
                };
                if (acceptHeaders != null)
                {
                    foreach (var header in acceptHeaders)
                    {
                        http2Request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(header));
                    }
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
            dynamic dynamicManifest;
            try
            {
                dynamicManifest = Newtonsoft.Json.Linq.JObject.Parse(manifestContents);
            }
            catch (Newtonsoft.Json.JsonReaderException invalidJsonError)
            {
                logger.LogError(invalidJsonError, "Unable to fetch manifest because manifest contains invalid JSON.");
                throw new ManifestContainsInvalidJsonException($"The manifest contains invalid JSON. {invalidJsonError.Message}", invalidJsonError);
            }

            try
            {
                var parsedManifest = JsonSerializer.Deserialize<WebAppManifest>(manifestContents);
                return (parsedManifest, dynamicManifest);
            }
            catch (JsonException jsonError)
            {
                logger.LogWarning(jsonError, "Error deserializing manifest JSON. Attempting deserialization while skipping invalid property types...");
                return DeserializeManifestWhileSkippingInvalidFields(dynamicManifest, manifestContents);
            }
            catch (Exception deserializeError)
            {
                deserializeError.Data.Add("manifestJson", manifestContents);
                logger.LogError(deserializeError, "Fetched manifest contents but was unable to deserialize it into an object. Raw JSON: \r\n\r\n{json}", manifestContents);
                throw;
            }
        }

        private (WebAppManifest parsedManifest, dynamic rawManifest) DeserializeManifestWhileSkippingInvalidFields(dynamic dynamicManifest, string manifestContents)
        {
            try
            {
                var manifestParseErrors = new List<Exception>();
                var parsedManifest = Newtonsoft.Json.JsonConvert.DeserializeObject<WebAppManifest>(manifestContents, new Newtonsoft.Json.JsonSerializerSettings
                {
                    Error = (sender, args) =>
                    {
                        logger.LogWarning(args.ErrorContext.Error, "Error deserializing manifest property {path}", args.ErrorContext.Path);
                        args.ErrorContext.Handled = true;
                        manifestParseErrors.Add(args.ErrorContext.Error);
                    }
                });

                // OK, did anything actually serialize? If not, throw.
                if (!parsedManifest.HasAnyNonNullProps())
                {
                    throw new AggregateException("Manifest didn't contain any valid properties. This suggests the manifest isn't valid JSON.", manifestParseErrors);
                }

                return (parsedManifest, dynamicManifest);
            }
            catch (Exception error)
            {
                logger.LogError(error, "Unable to parse manifest even while skipping invalid properties");
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

            // Do we have a base URL node? If so, use that base URL to resolve the manifest node.
            // This fixes https://github.com/pwa-builder/PWABuilder/issues/1843
            var baseNode = manifestNode.OwnerDocument?.DocumentNode.SelectSingleNode("//head/base");
            var baseNodeHref = baseNode?.Attributes["href"]?.Value;
            if (!string.IsNullOrWhiteSpace(baseNodeHref))
            {
                manifestHref = $"{baseNodeHref.TrimEnd('/')}/{manifestHref.TrimStart('/')}";
            }

            logger.LogInformation("Manifest node detected with href {href}", manifestHref);

            // Is the HREF the actual manifest data URL encoded?
            // See https://github.com/pwa-builder/PWABuilder/issues/1926
            var dataUrlPrefix = "data:application/manifest+json,";
            var isDataUrl = manifestHref.StartsWith(dataUrlPrefix, StringComparison.OrdinalIgnoreCase);
            if (isDataUrl)
            {
                logger.LogInformation("Manifest node href is data URL encoded string.");
                var manifestContents = Uri.UnescapeDataString(manifestHref[dataUrlPrefix.Length..]);
                return Task.FromResult(new ManifestContext(new Uri(manifestHref), manifestContents));
            }

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
                var manifestContents = await TryFetchWithHttpClient(manifestAbsoluteUrl, manifestMimeTypes);
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
                var manifestContents = await TryFetchWithHttpClient(localPathManifestUrl, manifestMimeTypes);
                if (!string.IsNullOrEmpty(manifestContents))
                {
                    return new ManifestContext(localPathManifestUrl, manifestContents);
                }
            }

            throw new ManifestNotFoundException($"Unable to detect manifest. Attempted manifest download at {manifestAbsoluteUrl} and {localPathManifestUrl}, but both failed.");
        }

        private async Task<HtmlDocument> LoadPage(Uri url)
        {
            var web = new HtmlWeb
            {
                CaptureRedirect = true,
                UserAgent = userAgent,
            };
            try
            {
                return await web.LoadFromWebAsync(url, null, null);
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
                html = await TryFetchWithHttpClient(this.url, "text/html");
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
