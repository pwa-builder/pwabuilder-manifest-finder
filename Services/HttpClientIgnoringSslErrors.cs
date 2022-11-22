using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class HttpClientIgnoringSslErrors : HttpClient
    {
        public HttpClientIgnoringSslErrors()
            : base(new HttpClientHandler
            {
                // Don't worry about HTTPS errors
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            })
        {
        }
    }
}
