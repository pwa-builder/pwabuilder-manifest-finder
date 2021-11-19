using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder
{

    [Serializable]
    public class HttpForbiddenException : Exception
    {
        public HttpForbiddenException() { }
        public HttpForbiddenException(string message) : base(message) { }
        public HttpForbiddenException(string message, Exception inner) : base(message, inner) { }
        protected HttpForbiddenException(
          System.Runtime.Serialization.SerializationInfo info,
          System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }
}
