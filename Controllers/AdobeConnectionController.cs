
using System.Security.Claims;
using System.Text.Json;
using DocuWareAdobeSignIntegration.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace DocuWareAdobeSignIntegration.Controllers;

[Authorize(AuthenticationSchemes =
    CookieAuthenticationDefaults.AuthenticationScheme)]
[Route("api/adobeconnection")]
public class AdobeConnectionController : Controller
{
    private readonly AdobeAuthService _authService;
    private readonly AdobeTokenStore _tokenStore;
    private readonly IDataProtector _stateProtector;
    private readonly ILogger<AdobeConnectionController> _logger;

    public AdobeConnectionController(
        AdobeAuthService authService,
        AdobeTokenStore tokenStore,
        IDataProtectionProvider protectionProvider,
        ILogger<AdobeConnectionController> logger)
    {
        _authService = authService;
        _tokenStore = tokenStore;
        _logger = logger;

        _stateProtector = protectionProvider.CreateProtector(
            "DocuWareAdobeSignIntegration.AdobeOAuthState.v1");
    }

    // ======================================================
    // Connect employee's Adobe Sign account
    // GET: /api/adobeconnection/connect
    // ======================================================

    [HttpGet("connect")]
    public IActionResult Connect()
    {
        var userId = GetCurrentUserId();

        var statePayload = JsonSerializer.Serialize(new OAuthState
        {
            UserId = userId,
            CreatedUtc = DateTime.UtcNow
        });

        var protectedState = _stateProtector.Protect(statePayload);

        var authorizationUrl =
            _authService.BuildAuthorizationUrl(protectedState);

        _logger.LogInformation(
            "Starting Adobe Sign authorization for employee ID: {UserId}",
            userId);

        return Redirect(authorizationUrl);
    }

    // ======================================================
    // Adobe Sign OAuth callback
    // GET: /api/adobeconnection/callback
    // ======================================================

    [AllowAnonymous]
    [HttpGet("callback")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        // Check whether Adobe Sign returned an authorization error.
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogWarning(
                "Adobe Sign authorization was declined or failed. Error: {Error}",
                error);

            return BadRequest(
                "Adobe Sign authorization was declined or failed.");
        }

        // Validate required callback parameters.
        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(state))
        {
            _logger.LogWarning(
                "Adobe Sign callback did not contain the required code and state.");

            return BadRequest(
                "The Adobe authorization response is incomplete.");
        }

        try
        {
            // Validate and decrypt the protected OAuth state.
            var payload = _stateProtector.Unprotect(state);

            var stateData =
                JsonSerializer.Deserialize<OAuthState>(payload);

            if (stateData == null ||
                string.IsNullOrWhiteSpace(stateData.UserId) ||
                stateData.CreatedUtc == default ||
                DateTime.UtcNow - stateData.CreatedUtc >
                    TimeSpan.FromMinutes(10) ||
                DateTime.UtcNow < stateData.CreatedUtc)
            {
                _logger.LogWarning(
                    "Adobe Sign callback received invalid or expired OAuth state.");

                return BadRequest(
                    "The Adobe authorization state is invalid or expired.");
            }

            // Recover the employee ID from the protected state.
            // This is the employee who initiated the connection.
            var employeeId = stateData.UserId;

            // If a DocuWare authentication cookie is available,
            // ensure it belongs to the employee who initiated the connection.
            if (User.Identity?.IsAuthenticated == true)
            {
                var currentUserId =
                    User.FindFirstValue(ClaimTypes.NameIdentifier) ??
                    User.FindFirstValue("sub");

                if (!string.IsNullOrWhiteSpace(currentUserId) &&
                    !string.Equals(
                        employeeId,
                        currentUserId,
                        StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "Adobe Sign OAuth state did not match the authenticated employee.");

                    return BadRequest(
                        "The Adobe authorization does not belong to the current employee.");
                }
            }

            _logger.LogInformation(
                "Adobe Sign authorization callback received for employee ID: {UserId}",
                employeeId);

            // Exchange the authorization code for Adobe tokens.
            var token = await _authService.ExchangeCodeForTokenAsync(
                code,
                cancellationToken);

            // Resolve the employee's Adobe API access point.
            var apiAccessPoint = token.ApiAccessPoint;

            if (string.IsNullOrWhiteSpace(apiAccessPoint))
            {
                apiAccessPoint = await _authService.GetBaseUriAsync(
                    token.AccessToken,
                    cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(apiAccessPoint))
            {
                throw new InvalidOperationException(
                    "Adobe Sign did not provide a valid API access point.");
            }

            // Store encrypted tokens against this employee's DocuWare ID.
            await _tokenStore.SaveAsync(
                employeeId,
                token,
                apiAccessPoint,
                cancellationToken);

            _logger.LogInformation(
                "Adobe Sign token successfully saved for employee ID: {UserId}",
                employeeId);

            // Temporary diagnostic response:
            // We will restore the normal application redirect after testing.
            return Content(
                "Adobe Sign connected successfully. Your encrypted tokens have been saved.");
        }
        catch (Exception ex)
        {
            // Log the actual exception for diagnosis.
            _logger.LogError(
                ex,
                "Adobe Sign connection callback failed.");

            // Temporary diagnostic response:
            // Do not redirect to the missing Signing page.
            return StatusCode(
                StatusCodes.Status500InternalServerError,
                "Adobe Sign connection failed. Check the application console for the detailed error.");
        }
    }

    // ======================================================
    // Disconnect employee's Adobe Sign account
    // POST: /api/adobeconnection/disconnect
    // ======================================================

    [HttpPost("disconnect")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        await _tokenStore.DeleteAsync(
            userId,
            cancellationToken);

        _logger.LogInformation(
            "Adobe Sign connection removed for employee ID: {UserId}",
            userId);

        return Content(
            "Your Adobe Sign connection has been removed.");
    }

    // ======================================================
    // Get authenticated DocuWare employee ID
    // ======================================================

    private string GetCurrentUserId()
    {
        var userId =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException(
                "Unable to identify the current DocuWare employee.");
        }

        return userId;
    }

    // ======================================================
    // Protected OAuth state model
    // ======================================================

    private sealed class OAuthState
    {
        public string UserId { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; }
    }
}
