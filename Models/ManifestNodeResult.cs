using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.PWABuilder.ManifestFinder.Models
{
    public class ManifestNodeResult
    {
        public ManifestNodeResult(HtmlNode primary, IEnumerable<HtmlNode> others)
        {
            this.PrimaryManifestNode = primary;
            this.AdditionalManifestNodes = others;
        }

        public HtmlNode PrimaryManifestNode { get; set; }
        public IEnumerable<HtmlNode> AdditionalManifestNodes { get; set; }
    }
}
