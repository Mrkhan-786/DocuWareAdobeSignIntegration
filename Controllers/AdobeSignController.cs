
using System.Text.Json;
using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocuWareAdobeSignIntegration.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdobeSignController : ControllerBase
{
    private readonly AdobeAgreementService _agreementService;
    private readonly AdobeAuthService _authService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdobeSignController> _logger;
    private readonly IWebHostEnvironment _environment;

    public AdobeSignController(
        AdobeAgreementService agreementService,
        AdobeAuthService authService,
        IConfiguration configuration,
        ILogger<AdobeSignController> logger,
        IWebHostEnvironment environment)
    {
        _agreementService = agreementService;
        _authService = authService;
        _configuration = configuration;
        _logger = logger;
        _environment = environment;
    }

    // =====================================================
    // HEALTH CHECK
    // =====================================================

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            Success = true,
            Message = "API is running"
        });
    }

    // =====================================================
    // SEND ENDPOINT TEST
    // =====================================================

    [HttpGet("sendtest")]
    public IActionResult SendTest()
    {
        return Ok(new
        {
            Success = true,
            Message = "Send endpoint reachable."
        });
    }

    // =====================================================
    // STEP 1: GENERATE ADOBE AUTHORIZATION URL
    // =====================================================

    [HttpGet("authorize")]
    public IActionResult GetAuthorizationUrl()
    {
        var clientId = _configuration["AdobeSign:ClientId"];
        var redirectUri = _configuration["AdobeSign:RedirectUri"];

        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(redirectUri))
        {
            _logger.LogError(
                "Adobe ClientId or RedirectUri is not configured.");

            return StatusCode(500, new
            {
                Success = false,
                Message = "Adobe ClientId or RedirectUri is not configured."
            });
        }

        var scope =
            "user_login:self agreement_read:account " +
            "agreement_write:account agreement_send:account";

        var authUrl =
            "https://secure.na3.adobesign.com/public/oauth/v2" +
            "?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={Uri.EscapeDataString(scope)}";

        return Ok(new
        {
            Success = true,
            AuthorizationUrl = authUrl
        });
    }

    // =====================================================
    // STEP 2: ADOBE OAUTH CALLBACK
    // =====================================================

    [HttpGet("callback")]
    public async Task<IActionResult> OAuthCallback(
        [FromQuery] string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return BadRequest(new
            {
                Success = false,
                Message = "Adobe authorization code is missing."
            });
        }

        try
        {
            _logger.LogInformation(
                "Adobe authorization callback received.");

            var token = await _authService
                .ExchangeCodeForTokenAsync(code);

            // Access and refresh tokens must never be returned
            // in this API response.

            _logger.LogInformation(
                "Adobe authentication completed.");

            return Ok(new
            {
                Success = true,
                Message = "Adobe authentication successful.",
                TokenType = token.TokenType,
                ExpiresIn = token.ExpiresIn
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Adobe token exchange failed.");

            return BadRequest(new
            {
                Success = false,
                Message = "Adobe authentication failed."
            });
        }
    }

    // =====================================================
    // STEP 3: ADOBE BASE URI
    //
    // Supply the token using:
    // Authorization: Bearer YOUR_ACCESS_TOKEN
    //
    // This avoids exposing the token in the URL.
    // =====================================================

    [HttpGet("baseuri")]
    public async Task<IActionResult> GetBaseUri(
        [FromHeader(Name = "Authorization")]
        string? authorizationHeader)
    {
        const string bearerPrefix = "Bearer ";

        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith(
                bearerPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized(new
            {
                Success = false,
                Message = "A Bearer access token is required in the Authorization header."
            });
        }

        var accessToken = authorizationHeader[
            bearerPrefix.Length..].Trim();

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return Unauthorized(new
            {
                Success = false,
                Message = "The access token is missing."
            });
        }

        try
        {
            var result = await _authService
                .GetBaseUriAsync(accessToken);

            using var jsonDocument = JsonDocument.Parse(result);

            return Ok(jsonDocument.RootElement.Clone());
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Adobe Base URI lookup failed.");

            return BadRequest(new
            {
                Success = false,
                Message = "Unable to retrieve Adobe Base URI."
            });
        }
    }

    // =====================================================
    // STEP 4: UPLOAD PDF TO ADOBE
    // DEVELOPMENT TEST ONLY
    // =====================================================

    [HttpGet("uploadtest")]
    public async Task<IActionResult> UploadTest()
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        try
        {
            var accessToken =
                _configuration["AdobeSign:TestAccessToken"];

            var pdfPath =
                _configuration["AdobeSign:TestPdfPath"];

            if (string.IsNullOrWhiteSpace(accessToken) ||
                string.IsNullOrWhiteSpace(pdfPath))
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = "Adobe test access token or PDF path is not configured."
                });
            }

            if (!System.IO.File.Exists(pdfPath))
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = "The configured test PDF could not be found."
                });
            }

            var transientDocumentId = await _agreementService
                .UploadTransientDocumentAsync(
                    accessToken,
                    pdfPath);

            return Ok(new
            {
                Success = true,
                Message = "Document uploaded successfully.",
                TransientDocumentId = transientDocumentId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Adobe upload test failed.");

            return StatusCode(500, new
            {
                Success = false,
                Message = "Adobe document upload test failed."
            });
        }
    }

    // =====================================================
    // STEP 5: CREATE ADOBE AGREEMENT
    // DEVELOPMENT TEST ONLY
    // =====================================================

    [HttpGet("createagreementtest")]
    public async Task<IActionResult> CreateAgreementTest()
    {
        if (!_environment.IsDevelopment())
        {
            return NotFound();
        }

        try
        {
            var accessToken =
                _configuration["AdobeSign:TestAccessToken"];

            var transientDocumentId =
                _configuration["AdobeSign:TestTransientDocumentId"];

            var recipientEmail =
                _configuration["AdobeSign:TestRecipientEmail"];

            if (string.IsNullOrWhiteSpace(accessToken) ||
                string.IsNullOrWhiteSpace(transientDocumentId) ||
                string.IsNullOrWhiteSpace(recipientEmail))
            {
                return BadRequest(new
                {
                    Success = false,
                    Message = "Adobe agreement test settings are incomplete."
                });
            }

            var agreementId = await _agreementService
                .CreateAdobeAgreementAsync(
                    accessToken,
                    transientDocumentId,
                    recipientEmail);

            return Ok(new
            {
                Success = true,
                Message = "Agreement created successfully.",
                AgreementId = agreementId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Adobe agreement creation test failed.");

            return StatusCode(500, new
            {
                Success = false,
                Message = "Adobe agreement creation test failed."
            });
        }
    }

    // =====================================================
    // STEP 6: DOCUWARE WORKFLOW -> ADOBE SIGN
    // =====================================================

    [HttpPost("send")]
    public async Task<IActionResult> SendForSignature(
        [FromBody] SignRequest? request)
    {
        if (request == null)
        {
            return BadRequest(new
            {
                Success = false,
                Message = "A signing request is required."
            });
        }

        if (!ModelState.IsValid)
        {
            _logger.LogWarning(
                "Signing request validation failed.");

            return ValidationProblem(ModelState);
        }

        try
        {
            _logger.LogInformation(
                "DocuWare workflow called the Adobe Sign API.");

            _logger.LogInformation(
                "Processing DocuWare Document ID: {DocumentId}",
                request.DocumentId);

            _logger.LogInformation(
                "Document Type: {DocumentType}",
                request.DocumentType);

            _logger.LogInformation(
                "Agreement Number: {AgreementNumber}",
                request.AgreementNumber);

            _logger.LogInformation(
                "Recipient Company: {RecipientCompany}",
                request.RecipientCompany);

            _logger.LogInformation(
                "Recipient Name: {RecipientName}",
                request.RecipientName);

            _logger.LogInformation(
                "Project Name: {ProjectName}",
                request.ProjectName);

            // Do not log the recipient email or access tokens.
            _logger.LogInformation(
                "Starting Adobe agreement processing.");

            var result = await _agreementService
                .CreateAgreementAsync(request);

            _logger.LogInformation(
                "Adobe agreement processing completed for DocuWare Document ID: {DocumentId}",
                request.DocumentId);

            return Ok(new
            {
                Success = true,
                Message = "Adobe agreement processing completed.",
                DocumentId = request.DocumentId,
                Result = result
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Adobe agreement processing failed for DocuWare Document ID: {DocumentId}",
                request.DocumentId);

            return StatusCode(500, new
            {
                Success = false,
                Message = "An error occurred while processing the Adobe agreement."
            });
        }
    }
}
