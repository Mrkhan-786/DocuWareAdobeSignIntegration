
using System.Security.Claims;
using DocuWareAdobeSignIntegration.Models;
using DocuWareAdobeSignIntegration.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DocuWareAdobeSignIntegration.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AdobeSignController : ControllerBase
{
    private readonly AdobeAgreementService _agreementService;
    private readonly AdobeUserTokenService _userTokenService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdobeSignController> _logger;
    private readonly IWebHostEnvironment _environment;

    public AdobeSignController(
        AdobeAgreementService agreementService,
        AdobeUserTokenService userTokenService,
        IConfiguration configuration,
        ILogger<AdobeSignController> logger,
        IWebHostEnvironment environment)
    {
        _agreementService = agreementService;
        _userTokenService = userTokenService;
        _configuration = configuration;
        _logger = logger;
        _environment = environment;
    }

    // =====================================================
    // HEALTH CHECK
    // =====================================================

    [AllowAnonymous]
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
    // DEVELOPMENT TEST: UPLOAD PDF TO ADOBE
    // =====================================================

    [HttpGet("uploadtest")]
    public async Task<IActionResult> UploadTest(
        CancellationToken cancellationToken)
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

            var apiAccessPoint =
                _configuration["AdobeSign:TestApiAccessPoint"];

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

            // Use the configured API access point when available.
            // Otherwise, use the development compatibility overload.
            var transientDocumentId =
                string.IsNullOrWhiteSpace(apiAccessPoint)
                    ? await _agreementService.UploadTransientDocumentAsync(
                        accessToken,
                        pdfPath)
                    : await _agreementService.UploadTransientDocumentAsync(
                        accessToken,
                        apiAccessPoint,
                        pdfPath,
                        cancellationToken);

            return Ok(new
            {
                Success = true,
                Message = "Document uploaded successfully.",
                TransientDocumentId = transientDocumentId
            });
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new
            {
                Success = false,
                Message = "The upload test was cancelled."
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
    // DEVELOPMENT TEST: CREATE ADOBE AGREEMENT
    // =====================================================

    [HttpGet("createagreementtest")]
    public async Task<IActionResult> CreateAgreementTest(
        CancellationToken cancellationToken)
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

            var apiAccessPoint =
                _configuration["AdobeSign:TestApiAccessPoint"];

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

            // Use the configured API access point when available.
            // Otherwise, use the development compatibility overload.
            var agreementId =
                string.IsNullOrWhiteSpace(apiAccessPoint)
                    ? await _agreementService.CreateAdobeAgreementAsync(
                        accessToken,
                        transientDocumentId,
                        recipientEmail)
                    : await _agreementService.CreateAdobeAgreementAsync(
                        accessToken,
                        apiAccessPoint,
                        transientDocumentId,
                        recipientEmail,
                        cancellationToken);

            return Ok(new
            {
                Success = true,
                Message = "Agreement created successfully.",
                AgreementId = agreementId
            });
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return StatusCode(499, new
            {
                Success = false,
                Message = "The agreement test was cancelled."
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
    // DOCUWARE WORKFLOW -> EMPLOYEE'S ADOBE SIGN ACCOUNT
    // =====================================================

    [HttpPost("send")]
    public async Task<IActionResult> SendForSignature(
        [FromBody] SignRequest? request,
        CancellationToken cancellationToken)
    {
        // ==============================================
        // STEP 1: VALIDATE REQUEST
        // ==============================================

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

        // ==============================================
        // STEP 2: IDENTIFY SIGNED-IN EMPLOYEE
        // ==============================================

        var userId =
            User.FindFirstValue(ClaimTypes.NameIdentifier) ??
            User.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(userId))
        {
            _logger.LogWarning(
                "Adobe agreement request received without an authenticated employee ID.");

            return Unauthorized(new
            {
                Success = false,
                Message = "Unable to identify the signed-in employee."
            });
        }

        try
        {
            _logger.LogInformation(
                "DocuWare workflow called the Adobe Sign API for employee {UserId}.",
                userId);

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

            // ==============================================
            // STEP 3: GET EMPLOYEE'S ADOBE SIGN TOKEN
            // ==============================================

            var adobeToken =
                await _userTokenService.GetValidTokenAsync(
                    userId,
                    cancellationToken);

            if (adobeToken == null ||
                string.IsNullOrWhiteSpace(adobeToken.AccessToken))
            {
                _logger.LogWarning(
                    "Adobe Sign token unavailable for employee {UserId}.",
                    userId);

                return Unauthorized(new
                {
                    Success = false,
                    Message = "Your Adobe Sign connection is unavailable. Please reconnect Adobe Sign."
                });
            }

            if (string.IsNullOrWhiteSpace(adobeToken.ApiAccessPoint))
            {
                _logger.LogWarning(
                    "Adobe API access point unavailable for employee {UserId}.",
                    userId);

                return Conflict(new
                {
                    Success = false,
                    Message = "Your Adobe API access point is missing. Please reconnect Adobe Sign."
                });
            }

            _logger.LogInformation(
                "Adobe Sign connection retrieved for employee {UserId}.",
                userId);

            // ==============================================
            // STEP 4: GET DOCUWARE ACCESS TOKEN
            // ==============================================

            var docuWareAccessToken =
                await HttpContext.GetTokenAsync("access_token");

            if (string.IsNullOrWhiteSpace(docuWareAccessToken))
            {
                _logger.LogWarning(
                    "DocuWare access token is missing for employee {UserId}.",
                    userId);

                return Unauthorized(new
                {
                    Success = false,
                    Message = "Your DocuWare session has expired or is unavailable. Please sign in to DocuWare again."
                });
            }

            _logger.LogInformation(
                "DocuWare access token retrieved for employee {UserId}.",
                userId);

            // ==============================================
            // STEP 5: CREATE ADOBE AGREEMENT
            // ==============================================

            // IMPORTANT:
            // CreateAgreementAsync requires SIX arguments:
            //
            // 1. SignRequest
            // 2. Employee user ID
            // 3. Adobe access token
            // 4. Adobe API access point
            // 5. DocuWare access token
            // 6. Cancellation token

            var result =
                await _agreementService.CreateAgreementAsync(
                    request,
                    userId,
                    adobeToken.AccessToken,
                    adobeToken.ApiAccessPoint,
                    docuWareAccessToken,
                    cancellationToken);

            // ==============================================
            // STEP 6: RETURN RESULT
            // ==============================================

            _logger.LogInformation(
                "Adobe agreement processing completed for DocuWare Document ID {DocumentId}.",
                request.DocumentId);

            return Ok(new
            {
                Success = true,
                Message = "Adobe agreement processing completed.",
                DocumentId = request.DocumentId,
                Result = result
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Adobe Sign connection or agreement configuration is unavailable for employee {UserId}.",
                userId);

            return Conflict(new
            {
                Success = false,
                Message = ex.Message
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "Authentication failed during agreement processing for employee {UserId}.",
                userId);

            return Unauthorized(new
            {
                Success = false,
                Message = "Authentication with DocuWare or Adobe Sign failed. Please reconnect your account."
            });
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Adobe agreement processing was cancelled for document {DocumentId}.",
                request.DocumentId);

            return StatusCode(499, new
            {
                Success = false,
                Message = "The signing request was cancelled."
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "An external API request failed during Adobe agreement processing for document {DocumentId}.",
                request.DocumentId);

            return StatusCode(502, new
            {
                Success = false,
                Message = "Communication with Adobe Sign or DocuWare failed. Please try again later."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Adobe agreement processing failed for DocuWare Document ID {DocumentId}.",
                request.DocumentId);

            return StatusCode(500, new
            {
                Success = false,
                Message = "An error occurred while processing the Adobe agreement."
            });
        }
    }
}
