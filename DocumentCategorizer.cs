namespace DocuWareAdobeSignIntegration.Helpers;

public record FolderItem(
    string Key,
    string Css,
    string Label,
    int Count,
    IReadOnlyList<string> StatusDots);

public class FolderCabinetModel
{
    public string Heading { get; init; } = "File cabinet";
    public IReadOnlyList<FolderItem> Folders { get; init; } = Array.Empty<FolderItem>();
}

public static class DocumentCategorizer
{
    public static readonly (string Name, string Css)[] Categories =
    {
        ("Financial", "financial"),
        ("Legal", "legal"),
        ("Human Resources", "human-resources"),
        ("Operations", "operations"),
        ("Project Management", "project-management"),
        ("Sales & Marketing", "sales-marketing"),
        ("Administration", "administration"),
        ("Compliance", "compliance"),
        ("Procurement", "procurement"),
        ("Customer Service", "customer-service")
    };

    // First match wins (same order as the original dashboard rules)
    private static readonly (string Name, string Css, string[] Terms)[] Rules =
    {
        ("Financial", "financial", new[] { "Financial", "Invoice", "Receipt", "Purchase_Order", "Purchase Order", "Quotation", "Budget", "Financial_Statement" }),
        ("Legal", "legal", new[] { "Legal", "Contract", "NDA", "Non_Disclosure", "Non-Disclosure", "Service_Level_Agreement", "Terms_and_Conditions" }),
        ("Human Resources", "human-resources", new[] { "Human_Resources", "Human Resources", "HR_", "Employment", "Job_Description", "Leave_Request", "Performance_Review", "Disciplinary" }),
        ("Operations", "operations", new[] { "Operations", "SOP", "Work_Instruction", "Incident", "Risk_Assessment" }),
        ("Project Management", "project-management", new[] { "Project", "Project_Charter", "Project_Plan", "Status_Report", "Meeting_Minutes" }),
        ("Sales & Marketing", "sales-marketing", new[] { "Sales", "Marketing", "Business_Proposal", "Brochure", "Customer_Agreement" }),
        ("Administration", "administration", new[] { "Administration", "Memo", "Business_Letter", "Policy", "Annual_Report" }),
        ("Compliance", "compliance", new[] { "Compliance", "Audit", "Certificate", "Regulatory" }),
        ("Procurement", "procurement", new[] { "Procurement", "Vendor", "Supplier", "RFP", "Request_for_Proposal" }),
        ("Customer Service", "customer-service", new[] { "Customer", "Service_Request", "Complaint", "Feedback" })
    };

    /// <summary>Uses the DocuWare category when set, otherwise guesses from the document name.</summary>
    public static (string Name, string Css) Resolve(string? category, string? categoryClass, string? name)
    {
        var cat = category ?? "Uncategorised";
        var css = categoryClass ?? "uncategorised";

        if (!string.IsNullOrWhiteSpace(cat) &&
            !string.Equals(cat, "Uncategorised", StringComparison.OrdinalIgnoreCase))
        {
            return (cat, css);
        }

        var docName = name ?? string.Empty;

        foreach (var rule in Rules)
        {
            if (rule.Terms.Any(t => docName.Contains(t, StringComparison.OrdinalIgnoreCase)))
            {
                return (rule.Name, rule.Css);
            }
        }

        return ("Uncategorised", "uncategorised");
    }

    /// <summary>Colour-dot name for a status: use as the CSS class st-{colour}.</summary>
    public static string StatusColor(string? status) => status switch
    {
        "Awaiting Signature" => "amber",
        "In Progress" => "blue",
        "Signed" => "violet",
        "Completed" => "green",
        "Failed" => "red",
        _ => "slate"
    };

    /// <summary>Builds the folder list (All + each category + Uncategorised) from resolved category names and statuses.</summary>
    public static FolderCabinetModel BuildFolders(
        IEnumerable<(string Category, string? Status)> docs,
        string heading = "File cabinet")
    {
        var list = docs.ToList();

        FolderItem Make(string key, string css, string label, List<(string Category, string? Status)> items) =>
            new(key, css, label, items.Count,
                items.Select(d => StatusColor(d.Status)).Distinct().Take(5).ToList());

        List<(string Category, string? Status)> For(string name) =>
            list.Where(d => string.Equals(d.Category, name, StringComparison.OrdinalIgnoreCase)).ToList();

        var folders = new List<FolderItem> { Make("all", "all", "All categories", list) };

        foreach (var (name, css) in Categories)
        {
            folders.Add(Make(name, css, name, For(name)));
        }

        folders.Add(Make("Uncategorised", "uncategorised", "Uncategorised", For("Uncategorised")));

        return new FolderCabinetModel { Heading = heading, Folders = folders };
    }
}