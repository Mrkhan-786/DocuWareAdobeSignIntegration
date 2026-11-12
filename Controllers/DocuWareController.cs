
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;

namespace DocuWareAdobeSignIntegration.Controllers;

[ApiController]
[Route("api/docuware")]
public class DocuWareController : ControllerBase
{
    // ======================================================
    // START DOCUWARE OAUTH LOGIN
    // ======================================================

    [HttpGet("login")]
    public IActionResult Login(
        [FromQuery] string? returnUrl = "/")
    {
        // Only allow local return URLs.
        if (string.IsNullOrWhiteSpace(returnUrl) ||
            !Url.IsLocalUrl(returnUrl))
        {
            returnUrl = "/";
        }

        var properties = new AuthenticationProperties
        {
            RedirectUri = returnUrl
        };

        return Challenge(properties, "DocuWare");
    }
}
