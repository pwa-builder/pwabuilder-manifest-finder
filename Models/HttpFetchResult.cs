using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class HttpFetchResult
    {
        public string? Result { get; set; }
        public Exception? Error { get; set; }
    }
}
