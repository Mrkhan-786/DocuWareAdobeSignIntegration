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

    public AdobeSignController(
        AdobeAgreementService agreementService,
        AdobeAuthService authService,
        IConfiguration configuration,
        ILogger<AdobeSignController> logger)
    {
        _agreementService = agreementService;
        _authService = authService;
        _configuration = configuration;
        _logger = logger;
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
    // STEP 1
    // Generate Adobe Authorization URL
    // =====================================================

    [HttpGet("authorize")]
    public IActionResult GetAuthorizationUrl()
    {
        var clientId =
            _configuration["AdobeSign:ClientId"];

        var redirectUri =
            _configuration["AdobeSign:RedirectUri"];

        var authUrl =
            $"https://secure.na3.adobesign.com/public/oauth/v2" +
            $"?response_type=code" +
            $"&client_id={clientId}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri!)}" +
            $"&scope=user_login:self agreement_read:account agreement_write:account agreement_send:account";

        return Ok(new
        {
            AuthorizationUrl = authUrl
        });
    }

    // =====================================================
    // STEP 2
    // Adobe OAuth Callback
    // =====================================================

    [HttpGet("callback")]
    public async Task<IActionResult> OAuthCallback(
        string code)
    {
        try
        {
            _logger.LogInformation(
                "Adobe authorization code received.");

            var token =
                await _authService
                    .ExchangeCodeForTokenAsync(code);

            return Ok(new
            {
                Success = true,
                Message = "Adobe authentication successful.",
                AccessToken = token.AccessToken,
                RefreshToken = token.RefreshToken,
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
                Error = ex.Message
            });
        }
    }

    // =====================================================
    // STEP 3
    // Adobe Base URI
    // =====================================================

    [HttpGet("baseuri")]
    public async Task<IActionResult> GetBaseUri(
        string accessToken)
    {
        try
        {
            var result =
                await _authService
                    .GetBaseUriAsync(accessToken);

            return Ok(
                JsonDocument.Parse(result)
                .RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Adobe Base URI lookup failed.");

            return BadRequest(new
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    // =====================================================
    // STEP 4
    // Upload PDF To Adobe
    // =====================================================

    [HttpGet("uploadtest")]
    public async Task<IActionResult> UploadTest()
    {
        try
        {
            string accessToken =
                "3AAABLblqZhAu7PbUURPBuqK7vaf5SWrBykCN0hVARvFu_r0eI-sFL4Yn15DTlhYr1KSLdbGMGJ8sN1S81Ab60Ynx79hAAa8f";

            var transientDocumentId =
                await _agreementService
                    .UploadTransientDocumentAsync(
                        accessToken,
                        @"C:\Windows\Temp\Adobe Sign Request Form.pdf");

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

            return BadRequest(new
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    // =====================================================
    // STEP 5
    // Create Adobe Agreement
    // =====================================================

    [HttpGet("createagreementtest")]
    public async Task<IActionResult> CreateAgreementTest()
    {
        try
        {
            string accessToken =
                "3AAABLblqZhAu7PbUURPBuqK7vaf5SWrBykCN0hVARvFu_r0eI-sFL4Yn15DTlhYr1KSLdbGMGJ8sN1S81Ab60Ynx79hAAa8f";

            string transientDocumentId =
                "CBSCTBABDUAAABACAABAAxLt0cMnwO82mOeUcj7OQ_DkG-mK2Fp8nnRlTfdg-WwQ86vFzqlsDgeH5vl08h7wX2wNm8Hc2MWDHRQurBqgPgxEkuzoUvL99LQh3gFc-tyJMDeHParlGJJDfsuFETHB5TBB_Frz3NLolia4dohrFVX0jF4CSFbVcLoPn7D92KkUtkEzK9Q_M8C0VCdtuSFqp8ZLd9adCQ8hUV-FBqcAcM7LlMhN7ORK5bMj5XT4TSVpB88eNFeYsBjZpvlOYB-jNIaTynt-I4C5hhQnuVGzaO8KKMz0BKP-3D-YBmCBWj9J_v4p46WN6nmOjHMO7ZODTqyTo0qzt60IQu6vtzr_pxEJga82NEquZeP49dga6DRB9zB10Oy4oSQHd6o3V40OA";

            var agreementId =
                await _agreementService
                    .CreateAdobeAgreementAsync(
                        accessToken,
                        transientDocumentId,
                        "thomas.rain@flyingtom.com");

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
                "Adobe agreement creation failed.");

            return BadRequest(new
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    // =====================================================
    // STEP 6
    // DocuWare → Adobe Sign
    // =====================================================

    [HttpPost("send")]
    public async Task<IActionResult> SendForSignature(
        [FromBody] SignRequest request)
    {
        _logger.LogInformation(
            "DocuWare workflow called the API.");

        _logger.LogInformation(
            "DocumentId: {DocumentId}",
            request.DocumentId);

        _logger.LogInformation(
            "DocumentType: {DocumentType}",
            request.DocumentType);

        _logger.LogInformation(
            "AgreementNumber: {AgreementNumber}",
            request.AgreementNumber);

        _logger.LogInformation(
            "RecipientCompany: {RecipientCompany}",
            request.RecipientCompany);

        _logger.LogInformation(
            "RecipientName: {RecipientName}",
            request.RecipientName);

        _logger.LogInformation(
            "RecipientEmail: {RecipientEmail}",
            request.RecipientEmail);

        _logger.LogInformation(
            "ProjectName: {ProjectName}",
            request.ProjectName);

        if (!ModelState.IsValid)
        {
            _logger.LogWarning(
                "Model validation failed.");

            return BadRequest(ModelState);
        }

        if (string.IsNullOrWhiteSpace(
                request.RecipientEmail))
        {
            _logger.LogWarning(
                "Recipient Email is missing.");

            return BadRequest(new
            {
                Success = false,
                Message = "Recipient Email is required."
            });
        }

        _logger.LogInformation(
            "Received agreement request for {Email}",
            request.RecipientEmail);

        var result =
            await _agreementService
                .CreateAgreementAsync(request);

        _logger.LogInformation(
            "Agreement processing completed.");

        return Ok(result);
    }
}