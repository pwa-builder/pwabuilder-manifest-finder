using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder.Models
{
    /// <summary>
    /// Manifest detection options.
    /// </summary>
    public enum ManifestDetectionOptions
    {
        /// <summary>
        /// Detects the first manifest only, ignoring additiona manifest declarations.
        /// </summary>
        First,
        /// <summary>
        /// Detects all manifests, for example, those for different translations.
        /// </summary>
        All
    }
}
