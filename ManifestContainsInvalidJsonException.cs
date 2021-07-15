using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{
    [Serializable]
    public class ManifestContainsInvalidJsonException : Exception
    {
        public ManifestContainsInvalidJsonException() { }
        public ManifestContainsInvalidJsonException(string message) : base(message) { }
        public ManifestContainsInvalidJsonException(string message, Exception inner) : base(message, inner) { }
        protected ManifestContainsInvalidJsonException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
