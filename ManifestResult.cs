using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    public class ManifestResult
    {
        /// <summary>
        /// The manifest's URL.
        /// </summary>
        public Uri? ManifestUrl { get; set; }

        /// <summary>
        /// The manifest score details.
        /// </summary>
        public Dictionary<string, int>? ManifestScore { get; set; }

        /// <summary>
        /// The manifest object.
        /// </summary>
        public dynamic? ManifestContents { get; set; }

        /// <summary>
        /// The error that occurred during manifest detection.
        /// </summary>
        public string? Error { get; set; }

        /// <summary>
        /// Gets whether the manifest contains invalid JSON.
        /// </summary>
        public bool ManifestContainsInvalidJson { get; set; }
    }
}
