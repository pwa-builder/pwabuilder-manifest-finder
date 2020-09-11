using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class ManifestResult
    {
        public Uri? ManifestUrl { get; set; }
        public string? ManifestContents { get; set; }
        public string? Error { get; set; }
    }
}
