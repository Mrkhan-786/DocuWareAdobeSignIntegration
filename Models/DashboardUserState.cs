
using System.ComponentModel.DataAnnotations;

namespace DocuWareAdobeSignIntegration.Models;

public class DashboardUserState
{
    [Key]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public DateTime? DashboardClearedAtUtc { get; set; }
}
