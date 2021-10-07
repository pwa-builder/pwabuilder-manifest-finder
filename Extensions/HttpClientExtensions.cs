using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public static class HttpClientExtensions
    {
        public static void AddAcceptHeaders(this HttpRequestMessage httpRequest, IEnumerable<string> acceptHeaders)
        {
            foreach (var header in acceptHeaders)
            {
                httpRequest.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue(header));
            }
        }

        public static void SetUserAgent(this HttpClient http, string userAgent)
        {
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        }
    }
}
