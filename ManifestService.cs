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
        private const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/94.0.4606.71 Safari/537.36 Edg/94.0.992.38 curl/7.64.1";
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
            var deserializationResult = DeserializeManifest(manifestContext.Json);
            var manifestScore = GetManifestScore(deserializationResult.Manifest);
            return new ManifestResult
            {
                ManifestUrl = manifestContext.Uri,
                ManifestScore = manifestScore,
                ManifestContainsInvalidJson = deserializationResult.InvalidJson,
                Warnings = deserializationResult.Warnings,
                Error = deserializationResult.Error?.ToString(),
                ManifestContents = deserializationResult.RawManifest // raw manifest here, otherwise we end up with null values for things that should be undefined, which throws some of our tooling (e.g. web package generator) for a loop.
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

        /// <summary>
        /// Attempts to fetch a resource at the specified URL.
        /// If the fetch fails, it will attempt to fetch using HTTP/2.
        /// Failures due to encoding errors will also attempt fetch using UTF-8 encoding as a fallback.
        /// If all fetches fail, the result will contain the exception.
        /// </summary>
        /// <param name="url"></param>
        /// <param name="acceptHeaders"></param>
        /// <returns></returns>
        private async Task<HttpFetchResult> TryFetch(Uri url, params string[] acceptHeaders)
        {
            try
            {
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                httpRequest.AddAcceptHeaders(acceptHeaders);
                var httpResponse = await http.SendAsync(httpRequest);

                // If it's a 403, we have special handling for this.
                if (httpResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var errorMessage = !string.IsNullOrWhiteSpace(httpResponse.ReasonPhrase) ?
                        httpResponse.ReasonPhrase :
                        "Web server's response was 403 Forbidden.";
                    throw new HttpForbiddenException(errorMessage);
                }

                httpResponse.EnsureSuccessStatusCode();
                var content = await httpResponse.Content.ReadAsStringAsync();
                return new HttpFetchResult
                {
                    Result = content
                };
            }
            catch (InvalidOperationException invalidOpError) when (invalidOpError.Message.Contains("The character set provided in ContentType is invalid."))
            {
                // Invalid encoding? Sometimes webpages have incorrectly set their charset / content type.
                // See if we can just parse the thing using UTF-8.
                logger.LogWarning(invalidOpError, "Unable to parse using HTTP client due to invalid ContentType. Attempting to parse using UTF-8.");
                return await TryFetchWithForcedUtf8(url, acceptHeaders);
            }
            catch (HttpForbiddenException forbiddenError)
            {
                logger.LogWarning(forbiddenError, "Received 403 Forbidden when fetching {url}. Attempting fetch with user agent fallback.");
                return await TryFetchWithoutCurlUserAgent(url, acceptHeaders);
            }
            catch (Exception httpException)
            {
                logger.LogWarning(httpException, "Failed to fetch {url} using non-curl user agent. Falling back to HTTP/2 fetch.", url);
                return await TryFetchWithHttp2Client(url, acceptHeaders);
            }
        }

        private async Task<HttpFetchResult> TryFetchWithForcedUtf8(Uri url, params string[] acceptHeaders)
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
                return new HttpFetchResult
                {
                    Result = responseString
                };
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Unable to parse HTML using forced UTF-8 mode.");
                return new HttpFetchResult
                {
                    Error = error
                };
            }
        }

        private async Task<HttpFetchResult> TryFetchWithHttp2Client(Uri url, params string[] acceptHeaders)
        {
            try
            {
                using var http2Request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = new Version(2, 0)
                };
                http2Request.AddAcceptHeaders(acceptHeaders);
                using var result = await http.SendAsync(http2Request);
                result.EnsureSuccessStatusCode();
                var contentString = await result.Content.ReadAsStringAsync();
                logger.LogInformation("Successfully fetched {url} via HTTP/2 fallback", url);
                return new HttpFetchResult
                {
                    Result = contentString
                };
            }
            catch (Exception http2Error)
            {
                logger.LogWarning(http2Error, "Unable to fetch {url} using HTTP/2 fallback.", url);
                return new HttpFetchResult
                {
                    Error = http2Error
                };
            }
        }

        /// <summary>
        /// Sets the user agent to exclude CURL, which some sites (e.g. Facebook) require for programmatic fetching to work.
        /// </summary>
        /// <param name="url">The URL to fetch</param>
        /// <param name="acceptHeaders">Additional headers to set on the fetch request.</param>
        /// <returns>A fetch result containing the fetch response.</returns>
        private async Task<HttpFetchResult> TryFetchWithoutCurlUserAgent(Uri url, string[] acceptHeaders)
        {
            try
            {
                // Append CURL to the user agent.
                http.SetUserAgent(userAgent.Replace("curl/7.64.1", string.Empty));

                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, url);
                httpRequest.AddAcceptHeaders(acceptHeaders);
                var httpResponse = await http.SendAsync(httpRequest);
                httpResponse.EnsureSuccessStatusCode();
                var content = await httpResponse.Content.ReadAsStringAsync();
                return new HttpFetchResult
                {
                    Result = content
                };
            }
            catch (Exception fetchError)
            {
                logger.LogWarning(fetchError, "Unable to fetch {url} using non-CURL user agent fallback.", url);
                return new HttpFetchResult
                {
                    Error = fetchError
                };
            }
            finally
            {
                // Reset the user agent back to the default user agent.
                http.SetUserAgent(userAgent);
            }
        }

        private ManifestDeserializationResult DeserializeManifest(string manifestContents)
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
                return new ManifestDeserializationResult
                {
                    Error = invalidJsonError,
                    InvalidJson = true
                };
            }

            try
            {
                var parsedManifest = JsonSerializer.Deserialize<WebAppManifest>(manifestContents);
                return new ManifestDeserializationResult
                {
                    Manifest = parsedManifest,
                    RawManifest = dynamicManifest
                };
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

        private ManifestDeserializationResult DeserializeManifestWhileSkippingInvalidFields(dynamic dynamicManifest, string manifestContents)
        {
            var manifestFieldErrors = new Dictionary<string, List<string>>();
            try
            {
                var parsedManifest = Newtonsoft.Json.JsonConvert.DeserializeObject<WebAppManifest>(manifestContents, new Newtonsoft.Json.JsonSerializerSettings
                {
                    Error = (sender, args) =>
                    {
                        logger.LogWarning(args.ErrorContext.Error, "Unable to deserialize manifest property {path}", args.ErrorContext.Path);
                        args.ErrorContext.Handled = true;
                        var manifestField = args.ErrorContext.Path ?? args.ErrorContext.Member as string ?? "manifest";
                        var errorMessage = args.ErrorContext.Error?.Message ?? "unknown error";

                        // Is it "[type] could not be converted to [list]? If so, shower a nicer Javascript-y warning
                        var isMessageArrayError = errorMessage.StartsWith("Error converting value", StringComparison.InvariantCultureIgnoreCase) && 
                        errorMessage.Contains("to type 'System.Collections.Generic.List", StringComparison.InvariantCultureIgnoreCase);
                        if (isMessageArrayError)
                        {
                            errorMessage = "Must be an array. " + errorMessage;
                        }

                        manifestFieldErrors.AddOrUpdate(manifestField, errorMessage);
                    }
                });

                // OK, did anything actually serialize? If not, throw.
                if (!parsedManifest.HasAnyNonNullProps())
                {
                    var badJsonError = new Exception("Attempted to parse manifest while skipping invalid fields, but all the manifest fields were null. This suggests the manifest is invalid JSON.");
                    logger.LogError(badJsonError, "Attempted to parse manifest while skipping invalid fields, but all the manifest fields were null. This suggests the manifest is invalid JSON. Details: {warnings}", manifestFieldErrors);
                    return new ManifestDeserializationResult
                    {
                        InvalidJson = true,
                        Warnings = manifestFieldErrors,
                        Error = badJsonError
                    };
                }

                return new ManifestDeserializationResult
                {
                    Manifest = parsedManifest,
                    RawManifest = dynamicManifest,
                    InvalidJson = false,
                    Warnings = manifestFieldErrors
                };
            }
            catch (Exception error)
            {
                logger.LogError(error, "Unable to parse manifest even while skipping invalid properties due to error. Warnings encountered: {warnings}", manifestFieldErrors);
                throw;
            }
        }

        private Dictionary<string, int> GetManifestScore(WebAppManifest? manifest)
        {
            var requiredFields = new[]
            {
                ("icons", 5, manifest?.Icons?.Count > 0),
                ("name", 5, !string.IsNullOrWhiteSpace(manifest?.Name)),
                ("short_name", 5, !string.IsNullOrWhiteSpace(manifest?.ShortName)),
                ("start_url", 5, !string.IsNullOrWhiteSpace(manifest?.StartUrl))
            };
            var recommendedFields = new[]
            {
                ("display", 2, !string.IsNullOrWhiteSpace(manifest?.Display) && WebAppManifest.DisplayTypes.Contains(manifest.Display)),
                ("background_color", 2, !string.IsNullOrWhiteSpace(manifest?.BackgroundColor)),
                ("description", 2, !string.IsNullOrWhiteSpace(manifest?.Description)),
                ("orientation", 2, !string.IsNullOrWhiteSpace(manifest?.Orientation) && WebAppManifest.OrientationTypes.Contains(manifest?.Orientation)),
                ("screenshots", 2, manifest?.Screenshots?.Count > 0),
                ("large_square_png_icon", 2, manifest?.Icons?.Any(i => i.IsAnyPurpose() && i.IsPng() && i.IsSquare() && i.GetLargestDimension()?.height >= 512) == true),
                ("maskable_icon", 2, manifest?.Icons?.Any(i => i.GetPurposes().Contains("maskable", StringComparer.InvariantCultureIgnoreCase)) == true),
                ("categories", 2, manifest?.Categories?.Count > 0),
                ("shortcuts", 2, manifest?.Shortcuts?.Count > 0)
            };
            var optionalFields = new[]
            {
                ("iarc_rating_id", 1, !string.IsNullOrWhiteSpace(manifest?.IarcRatingId)),
                ("prefer_related_applications", 1, manifest?.PreferRelatedApplications.HasValue == true),
                ("related_applications", 1, manifest?.RelatedApplications != null)
            };

            return new Dictionary<string, int>(requiredFields
                .Concat(recommendedFields)
                .Concat(optionalFields)
                .Concat(new[] { ("manifest", 10, manifest != null) }) // 10 points for having a manifest
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
            if (!string.IsNullOrWhiteSpace(baseNodeHref) && baseNodeHref != "/")
            {
                //manifestHref = $"{baseNodeHref.TrimEnd('/')}/{manifestHref.TrimStart('/')}";
                // We have a base node URL. 
                // Resolve the manifest from that. See https://github.com/pwa-builder/PWABuilder/issues/2102
                var baseUrl = new Uri(baseNodeHref, UriKind.RelativeOrAbsolute);
                var root = new Uri(this.url, baseUrl);
                manifestHref = new Uri(root, new Uri(manifestHref, UriKind.RelativeOrAbsolute)).ToString();
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
                var manifestFetch = await TryFetch(manifestAbsoluteUrl, manifestMimeTypes);
                if (!string.IsNullOrEmpty(manifestFetch.Result))
                {
                    return new ManifestContext(manifestAbsoluteUrl, manifestFetch.Result);
                }

                logger.LogWarning(manifestFetch.Error, "Unable to download manifest using absolute URL {url}. Falling back to local path detection.", manifestAbsoluteUrl);
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
                var manifestFetch = await TryFetch(localPathManifestUrl, manifestMimeTypes);
                if (!string.IsNullOrEmpty(manifestFetch.Result))
                {
                    return new ManifestContext(localPathManifestUrl, manifestFetch.Result);
                }
            }

            throw new ManifestNotFoundException($"Unable to detect manifest. Attempted manifest download at {manifestAbsoluteUrl} and {localPathManifestUrl}, but both failed.");
        }

        private async Task<HtmlDocument> LoadPage(Uri url)
        {
            var htmlFetch = await TryFetch(url, "text/html");
            if (htmlFetch.Error != null)
            {
                throw htmlFetch.Error;
            }

            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(htmlFetch.Result ?? string.Empty);
            return htmlDoc;
        }

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClientIgnoringSslErrors();
            http.SetUserAgent(userAgent);
            return http;
        }
    }
}
