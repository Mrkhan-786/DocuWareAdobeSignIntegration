
using System.Text.Json;

using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DocuWareAdobeSignIntegration.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/adobesign/webhook")]
public class AdobeSignWebhookController : ControllerBase
{
    private const string AdobeClientIdHeader = "X-AdobeSign-ClientId";

    private const string CompletedEvent = "AGREEMENT_WORKFLOW_COMPLETED";
    private const string ActionRequestedEvent = "AGREEMENT_ACTION_REQUESTED";
    private const string ActionCompletedEvent = "AGREEMENT_ACTION_COMPLETED";

    private readonly IConfiguration _configuration;
    private readonly ApplicationDbContext _db;
    private readonly IAdobeAgreementArchiveService _agreementService;
    private readonly ILogger<AdobeSignWebhookController> _logger;

    public AdobeSignWebhookController(
        IConfiguration configuration,
        ApplicationDbContext db,
        IAdobeAgreementArchiveService agreementService,
        ILogger<AdobeSignWebhookController> logger)
    {
        _configuration = configuration;
        _db = db;
        _agreementService = agreementService;
        _logger = logger;
    }

    // ======================================================
    // ADOBE WEBHOOK VERIFICATION
    // ======================================================

    [HttpGet]
    public IActionResult VerifyWebhook()
    {
        var configuredClientId = _configuration["AdobeSign:ClientId"];

        if (string.IsNullOrWhiteSpace(configuredClientId))
        {
            _logger.LogError(
                "Adobe Sign webhook verification failed: Client ID is not configured.");

            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        if (!Request.Headers.TryGetValue(
                AdobeClientIdHeader,
                out var suppliedClientId))
        {
            _logger.LogWarning(
                "Adobe Sign webhook verification did not contain the Client ID header.");

            return Unauthorized();
        }

        if (!string.Equals(
                suppliedClientId.ToString(),
                configuredClientId,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Adobe Sign webhook verification contained an unrecognized Client ID.");

            return Unauthorized();
        }

        return Acknowledge(configuredClientId);
    }

    // ======================================================
    // RECEIVE ADOBE WEBHOOK NOTIFICATION
    // ======================================================

    [HttpPost]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> ReceiveNotification(
        CancellationToken cancellationToken)
    {
        var configuredClientId = _configuration["AdobeSign:ClientId"];

        if (string.IsNullOrWhiteSpace(configuredClientId))
        {
            _logger.LogError(
                "Adobe Sign webhook rejected: Client ID is not configured.");

            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        // 1. Validate Adobe's Client ID header.

        if (!Request.Headers.TryGetValue(
                AdobeClientIdHeader,
                out var suppliedClientId))
        {
            _logger.LogWarning(
                "Adobe Sign notification did not contain the Client ID header.");

            return Unauthorized();
        }

        if (!string.Equals(
                suppliedClientId.ToString(),
                configuredClientId,
                StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Adobe Sign notification contained an unrecognized Client ID.");

            return Unauthorized();
        }

        // 2. Read and parse the JSON payload.

        JsonDocument payload;

        try
        {
            payload = await JsonDocument.ParseAsync(
                Request.Body,
                cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "Adobe Sign webhook sent invalid JSON.");

            return BadRequest();
        }

        using (payload)
        {
            var root = payload.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return BadRequest();
            }

            // 3. Extract event information.

            var eventName =
                GetString(root, "event") ??
                GetString(root, "eventType");

            var agreementId =
                GetNestedString(root, "agreement", "id") ??
                GetString(root, "agreementId") ??
                GetString(root, "resourceId");

            var notificationId =
                GetString(root, "webhookNotificationId");

            if (string.IsNullOrWhiteSpace(eventName))
            {
                _logger.LogWarning(
                    "Adobe Sign notification did not contain an event name.");

                return BadRequest();
            }

            if (string.IsNullOrWhiteSpace(agreementId))
            {
                _logger.LogWarning(
                    "Adobe Sign event {EventName} did not contain an agreement ID.",
                    eventName);

                return BadRequest();
            }

            eventName = eventName.Trim();
            agreementId = agreementId.Trim();

            _logger.LogInformation(
                "Received Adobe Sign event {EventName} for agreement {AgreementId}. Notification: {NotificationId}.",
                eventName,
                agreementId,
                notificationId ?? "(not supplied)");

            // 4. Agreement sent to signer.

            if (string.Equals(
                    eventName,
                    ActionRequestedEvent,
                    StringComparison.OrdinalIgnoreCase))
            {
                await UpdateWorkflowStatusAsync(
                    agreementId,
                    "Awaiting Signature",
                    cancellationToken);

                return Acknowledge(configuredClientId);
            }

            // 5. One participant completed their signing action.
            // The entire agreement may still have other participants.

            if (string.Equals(
                    eventName,
                    ActionCompletedEvent,
                    StringComparison.OrdinalIgnoreCase))
            {
                await UpdateWorkflowStatusAsync(
                    agreementId,
                    "In Progress",
                    cancellationToken);

                return Acknowledge(configuredClientId);
            }

            // 6. Entire agreement completed.
            // Download the signed PDF and save it in the application's
            // local File Cabinet.

            if (string.Equals(
                    eventName,
                    CompletedEvent,
                    StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation(
                    "Adobe Sign confirmed completion of agreement {AgreementId}. Starting local File Cabinet save.",
                    agreementId);

                try
                {
                    await _agreementService.MarkAgreementSignedAsync(
                        agreementId,
                        notificationId,
                        cancellationToken);

                    _logger.LogInformation(
                        "Completed processing Adobe agreement {AgreementId}.",
                        agreementId);

                    return Acknowledge(configuredClientId);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Processing of Adobe agreement {AgreementId} was cancelled.",
                        agreementId);

                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Could not download and save the signed PDF for agreement {AgreementId}.",
                        agreementId);

                    // Return a failure response so Adobe can retry
                    // delivering the notification.
                    return StatusCode(
                        StatusCodes.Status500InternalServerError);
                }
            }

            // 7. Acknowledge other Adobe events without changing status.

            _logger.LogInformation(
                "Adobe Sign event {EventName} acknowledged without changing workflow status.",
                eventName);

            return Acknowledge(configuredClientId);
        }
    }

    // ======================================================
    // UPDATE NON-FINAL WORKFLOW STATUS
    // ======================================================

    private async Task UpdateWorkflowStatusAsync(
        string agreementId,
        string status,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        // Do not overwrite the status of a document that has already
        // been saved in the application's File Cabinet.
        //
        // ArchivedAtUtc is also checked to preserve legacy records
        // that were previously archived to DocuWare.

        var updated = await _db.AdobeAgreementWorkflows
            .Where(x =>
                x.AgreementId == agreementId &&
                x.ArchivedAtUtc == null &&
                (x.SignedPdfPath == null || x.SignedPdfPath == ""))
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.UpdatedAtUtc, now),
                cancellationToken);

        if (updated == 0)
        {
            var workflowExists = await _db.AdobeAgreementWorkflows
                .AsNoTracking()
                .AnyAsync(
                    x => x.AgreementId == agreementId,
                    cancellationToken);

            if (!workflowExists)
            {
                _logger.LogWarning(
                    "No workflow record exists for Adobe agreement {AgreementId}.",
                    agreementId);

                return;
            }

            _logger.LogInformation(
                "Agreement {AgreementId} is already saved or archived. Its workflow status was not changed.",
                agreementId);

            return;
        }

        _logger.LogInformation(
            "Workflow status updated. Agreement {AgreementId}, status {Status}.",
            agreementId,
            status);
    }

    // ======================================================
    // ACKNOWLEDGE ADOBE
    // ======================================================

    private IActionResult Acknowledge(string clientId)
    {
        Response.Headers[AdobeClientIdHeader] = clientId;

        return Ok(new
        {
            xAdobeSignClientId = clientId
        });
    }

    // ======================================================
    // JSON HELPERS
    // ======================================================

    private static string? GetString(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }

    private static string? GetNestedString(
        JsonElement element,
        string parentProperty,
        string childProperty)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(parentProperty, out var parent) &&
            parent.ValueKind == JsonValueKind.Object)
        {
            return GetString(parent, childProperty);
        }

        return null;
    }
}
