using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{

    [Serializable]
    public class ManifestNotFoundException : Exception
    {
        public ManifestNotFoundException() { }
        public ManifestNotFoundException(string message) : base(message) { }
        public ManifestNotFoundException(string message, Exception inner) : base(message, inner) { }
        protected ManifestNotFoundException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
