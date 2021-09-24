using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    /// <summary>
    /// Results from a manifest deserialization: the deserialized manifest, the raw dynamic manifest, and any warnings that occured during deserilaizaiton.
    /// </summary>
    public class ManifestDeserializationResult
    {
        /// <summary>
        /// Gets the parsed manifest. For undefined manifest values, the value will be set to null.
        /// </summary>
        public WebAppManifest? Manifest { get; set; }

        /// <summary>
        /// Gets raw dynamic manifest, which may have some manifest values as undefined.
        /// </summary>
        public dynamic? RawManifest { get; set; }

        /// <summary>
        /// Gets a flag indicating that the manifest JSON was invalid.
        /// </summary>
        public bool InvalidJson { get; set; }

        /// <summary>
        /// The error that occurred preventing deserialization.
        /// </summary>
        public Exception? Error { get; set; }

        /// <summary>
        /// Gets the warnings that occurred during manifest deserialization. 
        /// The keys are manifest properties, the values are the warning messages.
        /// </summary>
        public Dictionary<string, List<string>> Warnings { get; set; } = new Dictionary<string, List<string>>();
    }
}
