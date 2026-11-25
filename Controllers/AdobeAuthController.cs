
using System.Security.Claims;
using System.Text.Json;
using DocuWareAdobeSignIntegration.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace DocuWareAdobeSignIntegration.Controllers;

[ApiController]
[Route("api/adobesign")]
public class AdobeAuthController : ControllerBase
{
    private const string StateCookieName = "AdobeSign.OAuthState";

    private readonly AdobeAuthService _authService;
    private readonly AdobeTokenStore _tokenStore;
    private readonly IDataProtector _stateProtector;
    private readonly ILogger<AdobeAuthController> _logger;

    public AdobeAuthController(
        AdobeAuthService authService,
        AdobeTokenStore tokenStore,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<AdobeAuthController> logger)
    {
        _authService = authService;
        _tokenStore = tokenStore;
        _stateProtector = dataProtectionProvider.CreateProtector(
            "DocuWareAdobeSignIntegration.AdobeOAuthState.v1");

        _logger = logger;
    }

    [Authorize]
    [HttpGet("connect")]
    public IActionResult Connect()
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var statePayload = JsonSerializer.Serialize(
            new OAuthState
            {
                UserId = userId,
                CreatedUtc = DateTimeOffset.UtcNow
            });

        var protectedState = _stateProtector.Protect(statePayload);

        Response.Cookies.Append(
            StateCookieName,
            protectedState,
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                MaxAge = TimeSpan.FromMinutes(10),
                Path = "/api/adobesign/callback"
            });

        var authorizationUrl =
            _authService.BuildAuthorizationUrl(protectedState);

        return Redirect(authorizationUrl);
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            _logger.LogWarning(
                "Adobe Sign authorization was rejected. Error: {Error}",
                error);

            return Redirect(
                "/?adobeConnection=denied");
        }

        if (string.IsNullOrWhiteSpace(code) ||
            string.IsNullOrWhiteSpace(state))
        {
            return BadRequest(
                "Adobe Sign did not return the required authorization values.");
        }

        if (!Request.Cookies.TryGetValue(
            StateCookieName,
            out var cookieState) ||
            string.IsNullOrWhiteSpace(cookieState) ||
            !string.Equals(
                cookieState,
                state,
                StringComparison.Ordinal))
        {
            return BadRequest(
                "Adobe Sign authorization state validation failed. Please try connecting again.");
        }

        Response.Cookies.Delete(
            StateCookieName,
            new CookieOptions
            {
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/api/adobesign/callback"
            });

        OAuthState? stateData;

        try
        {
            var json = _stateProtector.Unprotect(state);

            stateData = JsonSerializer.Deserialize<OAuthState>(json);
        }
        catch (Exception ex) when (
            ex is System.Security.Cryptography.CryptographicException ||
            ex is JsonException)
        {
            _logger.LogWarning(
                "Adobe Sign returned an invalid OAuth state.");

            return BadRequest(
                "Adobe Sign authorization state is invalid. Please try again.");
        }

        if (stateData == null ||
            string.IsNullOrWhiteSpace(stateData.UserId) ||
            stateData.CreatedUtc < DateTimeOffset.UtcNow.AddMinutes(-10) ||
            stateData.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return BadRequest(
                "Adobe Sign authorization state has expired. Please try again.");
        }

        try
        {
            // Exchange the authorization code using the existing service.
            var token = await _authService.ExchangeCodeForTokenAsync(code);

            if (string.IsNullOrWhiteSpace(token.ApiAccessPoint))
            {
                throw new InvalidOperationException(
                    "Adobe Sign did not return an API access point.");
            }

            // Save encrypted tokens against the employee who started the flow.
            await _tokenStore.SaveAsync(
                stateData.UserId,
                token,
                token.ApiAccessPoint,
                cancellationToken);

            _logger.LogInformation(
                "Adobe Sign connection successfully saved for an employee.");

            return Redirect("/?adobeConnection=success");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Unable to complete Adobe Sign authorization.");

            return Redirect(
                "/?adobeConnection=failed");
        }
    }

    [Authorize]
    [HttpGet("status")]
    public async Task<IActionResult> Status(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var token = await _tokenStore.GetAsync(
            userId,
            cancellationToken);

        return Ok(new
        {
            connected = token != null,
            expiresUtc = token?.AccessTokenExpiresUtc
        });
    }

    [Authorize]
    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(
        CancellationToken cancellationToken)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        await _tokenStore.DeleteAsync(
            userId,
            cancellationToken);

        return Ok(new
        {
            success = true,
            message = "Adobe Sign has been disconnected."
        });
    }

    private string? GetCurrentUserId()
    {
        return User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub");
    }

    private sealed class OAuthState
    {
        public string UserId { get; set; } = string.Empty;

        public DateTimeOffset CreatedUtc { get; set; }
    }
}
