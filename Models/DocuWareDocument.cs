
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DocuWareAdobeSignIntegration.Models
{
    public class DocuWareDocument
    {
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? FileName { get; set; }

        public DateTimeOffset? CreatedAt { get; set; }

        public DateTimeOffset? ModifiedAt { get; set; }

        public string? Status { get; set; }

        public Dictionary<string, JsonElement> IndexFields { get; set; }
            = new Dictionary<string, JsonElement>();
    }
}
