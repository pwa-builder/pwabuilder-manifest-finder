using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class ManifestContext
    {
        public ManifestContext(Uri uri, string json)
        {
            this.Uri = uri;
            this.Json = json;
        }

        /// <summary>
        /// The absolute URI of the manifest.
        /// </summary>
        public Uri Uri { get; }

        /// <summary>
        /// The JSON contents of the manifest.
        /// </summary>
        public string Json { get; }
    }
}
