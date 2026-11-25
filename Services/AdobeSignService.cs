
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using DocuWareAdobeSignIntegration.Models;
using Microsoft.Extensions.Logging;

namespace DocuWareAdobeSignIntegration.Services;

public class AdobeSignService
{
    private const string ApiVersion = "v6";
    private const int MaxPdfSizeBytes = 100 * 1024 * 1024;
    private const int MaxTransientUploadSizeBytes = 10 * 1024 * 1024;
    private const int MaxAgreementReadyAttempts = 10;
    private const int MaxSendStatusAttempts = 60;
    private const int MaxHttpRetries = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AdobeSignService> _logger;

    public AdobeSignService(
        IHttpClientFactory httpClientFactory,
        ILogger<AdobeSignService> logger)
    {
        _httpClientFactory = httpClientFactory
            ?? throw new ArgumentNullException(nameof(httpClientFactory));

        _logger = logger
            ?? throw new ArgumentNullException(nameof(logger));
    }

    // ============================================================
    // 1. CREATE AN AGREEMENT IN AUTHORING STATE
    // ============================================================

    public async Task<AdobeSignAgreementResult> SendForSignatureAsync(
        Stream pdfStream,
        string fileName,
        List<AdobeSignRecipient> recipients,
        string subject,
        string? message,
        string signingOrderMode,
        List<AdobeSignAnchorField> anchorFields,
        string accessToken,
        string apiAccessPoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdfStream);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(anchorFields);

        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("A PDF filename is required.", nameof(fileName));

        if (recipients.Count == 0)
            throw new ArgumentException("At least one Adobe Sign recipient is required.", nameof(recipients));

        if (string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("An agreement name or email subject is required.", nameof(subject));

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("An Adobe Sign access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(apiAccessPoint))
            throw new ArgumentException("The Adobe Sign API access point is required.", nameof(apiAccessPoint));

        ValidateRecipients(recipients);

        if (!string.Equals(signingOrderMode, "Sequential", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(signingOrderMode, "Parallel", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Signing order must be Sequential or Parallel.",
                nameof(signingOrderMode));
        }

        var normalizedSigningOrder =
            string.Equals(signingOrderMode, "Parallel", StringComparison.OrdinalIgnoreCase)
                ? "Parallel"
                : "Sequential";

        var pdfBytes = await ReadPdfAsync(
            pdfStream,
            MaxTransientUploadSizeBytes,
            cancellationToken);

        var safeFileName = NormalizeFileName(fileName);
        var baseUrl = NormalizeApiAccessPoint(apiAccessPoint);

        _logger.LogInformation(
            "Starting Adobe Sign agreement creation. Filename: {FileName}; recipients: {RecipientCount}; signing order: {SigningOrder}.",
            safeFileName,
            recipients.Count,
            normalizedSigningOrder);

        var useFinancialRequestLayout = IsFinancialDocumentRequestTemplate(safeFileName);

        if (useFinancialRequestLayout && recipients.Count > 2)
        {
            throw new InvalidOperationException(
                "The supplied Financial_Document_Request.pdf template has two signing blocks. It supports signer 1 in the Authorised Signatory column and signer 2 in the Recipient Acknowledgement column. Use a template with additional signing blocks for more than two signers.");
        }

        // Upload the PDF as a transient document.
        var transientDocumentId = await UploadTransientDocumentAsync(
            baseUrl,
            pdfBytes,
            safeFileName,
            accessToken,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(transientDocumentId))
        {
            throw new InvalidOperationException(
                "Adobe Sign did not return a transient document ID.");
        }

        var participantSets = BuildParticipantSets(
            recipients,
            normalizedSigningOrder);

        // API v6 does not support formFieldGenerators in POST /agreements.
        // Form fields must be configured separately using the authoring APIs.
        var agreementPayload = new
        {
            fileInfos = new[]
            {
                new { transientDocumentId }
            },

            name = subject.Trim(),

            message = string.IsNullOrWhiteSpace(message)
                ? null
                : message.Trim(),

            participantSetsInfo = participantSets,

            signatureType = "ESIGN",

            state = "AUTHORING",

            locale = "en_US"
        };

        var agreementJson = JsonSerializer.Serialize(
            agreementPayload,
            JsonOptions);

        using var agreementResponse = await SendJsonRequestAsync(
            HttpMethod.Post,
            $"{baseUrl}/agreements",
            agreementJson,
            accessToken,
            cancellationToken);

        var agreementResponseBody =
            await agreementResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!agreementResponse.IsSuccessStatusCode)
        {
            throw CreateAdobeException(
                "create the Adobe Sign agreement",
                agreementResponse.StatusCode,
                agreementResponseBody);
        }

        var agreementId = ReadAgreementId(agreementResponseBody);

        if (string.IsNullOrWhiteSpace(agreementId))
        {
            throw new InvalidOperationException(
                "Adobe Sign accepted the agreement request but did not return an agreement ID. Response: " +
                LimitErrorText(agreementResponseBody));
        }

        _logger.LogInformation(
            "Adobe Sign agreement {AgreementId} was created in AUTHORING state.",
            agreementId);

        var agreementStatus = await WaitForAgreementReadyAsync(
            baseUrl,
            agreementId,
            accessToken,
            cancellationToken);

        if (useFinancialRequestLayout)
        {
            try
            {
                await AddFinancialRequestFormFieldsAsync(
                    baseUrl,
                    agreementId,
                    anchorFields,
                    recipients,
                    accessToken,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Adobe agreement {AgreementId} was created, but its signer-specific fields could not be placed.",
                    agreementId);

                throw new InvalidOperationException(
                    $"Adobe agreement {agreementId} was created in AUTHORING, but the application could not place the signer fields. Do not submit this document again yet. Open this agreement in Adobe Manage and check it before retrying. Details: {ex.Message}",
                    ex);
            }
        }
        else if (anchorFields.Count > 0)
        {
            _logger.LogWarning(
                "No automatic coordinate layout is configured for PDF {FileName}. The agreement {AgreementId} will open in Adobe AUTHORING without application-generated fields; place and assign fields in Adobe before clicking Send.",
                safeFileName,
                agreementId);
        }

        // This application uses an automatic-send workflow:
        // 1. Create the agreement in AUTHORING.
        // 2. Wait until Adobe has processed it.
        // 3. Add the signer-specific form fields.
        // 4. Return the agreement ID so the caller can persist the
        //    workflow record.
        // 5. The caller then transitions this same agreement to
        //    IN_PROCESS using SendAgreementForSignatureAsync.
        //
        // An AUTHORING browser URL is not required for that process.
        // Calling POST /agreements/{id}/views here made successful
        // agreement creation fail when Adobe temporarily returned
        // VIEW_NOT_AVAILABLE, even though the agreement itself was
        // already ready for the API send transition. It also prevented
        // the caller from saving the agreement ID to the database.
        _logger.LogInformation(
            "Adobe agreement {AgreementId} is prepared in AUTHORING with its form fields. The application will persist it and send this agreement through the API; an AUTHORING browser view is not required.",
            agreementId);

        return new AdobeSignAgreementResult
        {
            AgreementId = agreementId,
            Status = agreementStatus,
            AuthoringUrl = string.Empty
        };
    }

    // ============================================================
    // 2. SEND AN EXISTING AGREEMENT FOR SIGNATURE
    // ============================================================

    public async Task<string> SendAgreementForSignatureAsync(
        string agreementId,
        string accessToken,
        string apiAccessPoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agreementId))
            throw new ArgumentException("An Adobe agreement ID is required.", nameof(agreementId));

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("An Adobe Sign access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(apiAccessPoint))
            throw new ArgumentException("The Adobe Sign API access point is required.", nameof(apiAccessPoint));

        agreementId = agreementId.Trim();

        var currentStatus = await GetAgreementStatusAsync(
            agreementId,
            accessToken,
            apiAccessPoint,
            cancellationToken);

        if (IsAlreadySentOrCompleted(currentStatus))
        {
            _logger.LogInformation(
                "Adobe agreement {AgreementId} is already beyond AUTHORING. Current status: {Status}.",
                agreementId,
                currentStatus);

            return currentStatus;
        }

        if (!string.Equals(currentStatus, "AUTHORING", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Adobe agreement {agreementId} cannot be sent because its current status is {currentStatus}. Expected AUTHORING.");
        }

        var baseUrl = NormalizeApiAccessPoint(apiAccessPoint);

        var url =
            $"{baseUrl}/agreements/{Uri.EscapeDataString(agreementId)}/state";

        var payload = JsonSerializer.Serialize(
            new { state = "IN_PROCESS" },
            JsonOptions);

        using var response = await SendJsonRequestAsync(
            HttpMethod.Put,
            url,
            payload,
            accessToken,
            cancellationToken);

        var responseBody =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateAdobeException(
                "send the existing Adobe Sign agreement for signature",
                response.StatusCode,
                responseBody);
        }

        _logger.LogInformation(
            "Adobe accepted the request to send agreement {AgreementId} for signature. Waiting for its asynchronous state transition.",
            agreementId);

        // Adobe processes the AUTHORING -> IN_PROCESS transition
        // asynchronously. Do not treat the first GET response as final;
        // it can still report AUTHORING immediately after the successful PUT.
        string? updatedStatus = null;

        for (var attempt = 1; attempt <= MaxSendStatusAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            updatedStatus = await GetAgreementStatusAsync(
                agreementId,
                accessToken,
                apiAccessPoint,
                cancellationToken);

            if (IsAgreementSentStatus(updatedStatus))
            {
                _logger.LogInformation(
                    "Adobe agreement {AgreementId} is now in signing status {Status}. Attempt {Attempt}/{MaxAttempts}.",
                    agreementId,
                    updatedStatus,
                    attempt,
                    MaxSendStatusAttempts);

                return updatedStatus;
            }

            if (IsTerminalFailureStatus(updatedStatus))
            {
                throw new InvalidOperationException(
                    $"Adobe accepted the send request for agreement {agreementId}, but the agreement entered terminal status {updatedStatus}.");
            }

            _logger.LogInformation(
                "Adobe agreement {AgreementId} is still transitioning after the send request. Current status: {Status}. Attempt {Attempt}/{MaxAttempts}.",
                agreementId,
                updatedStatus,
                attempt,
                MaxSendStatusAttempts);

            if (attempt < MaxSendStatusAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Adobe accepted the send request for agreement {agreementId}, but its status did not reach a signing state within {MaxSendStatusAttempts * 2} seconds. Last status: {updatedStatus ?? "Unknown"}. Do not create another agreement; check this agreement in Adobe Manage before retrying.");
    }

    private static bool IsAgreementSentStatus(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "IN_PROCESS" => true,
            "OUT_FOR_SIGNATURE" => true,
            "OUT_FOR_SIGNING" => true,
            "AGREEMENT_VIEWED" => true,
            "SIGNED" => true,
            "APPROVED" => true,
            "COMPLETED" => true,
            _ => false
        };
    }

    private static bool IsAlreadySentOrCompleted(string? status)
    {
        return IsAgreementSentStatus(status);
    }

    // ============================================================
    // 3. VALIDATE RECIPIENTS
    // ============================================================

    private static void ValidateRecipients(List<AdobeSignRecipient> recipients)
    {
        if (recipients.Count > 10)
        {
            throw new ArgumentException(
                "A maximum of 10 signing recipients is supported.",
                nameof(recipients));
        }

        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipient in recipients)
        {
            if (recipient == null)
            {
                throw new ArgumentException(
                    "The recipient list contains an empty recipient.",
                    nameof(recipients));
            }

            if (string.IsNullOrWhiteSpace(recipient.Name))
            {
                throw new ArgumentException(
                    "Every recipient must have a name.",
                    nameof(recipients));
            }

            if (string.IsNullOrWhiteSpace(recipient.Email))
            {
                throw new ArgumentException(
                    "Every recipient must have an email address.",
                    nameof(recipients));
            }

            if (!System.Net.Mail.MailAddress.TryCreate(recipient.Email.Trim(), out _))
            {
                throw new ArgumentException(
                    $"The recipient email address '{recipient.Email}' is invalid.",
                    nameof(recipients));
            }

            if (!emails.Add(recipient.Email.Trim()))
            {
                throw new ArgumentException(
                    $"The email address '{recipient.Email}' has been assigned to more than one signer.",
                    nameof(recipients));
            }
        }
    }

    // ============================================================
    // 4. FINANCIAL DOCUMENT REQUEST FORM-FIELD PLACEMENT
    // ============================================================

    private static bool IsFinancialDocumentRequestTemplate(string fileName)
    {
        return string.Equals(
            Path.GetFileName(fileName),
            "Financial_Document_Request.pdf",
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task AddFinancialRequestFormFieldsAsync(
        string baseUrl,
        string agreementId,
        List<AdobeSignAnchorField> anchorFields,
        List<AdobeSignRecipient> recipients,
        string accessToken,
        CancellationToken cancellationToken)
    {
        // A participant-set ID is required in the FormField.assignee
        // property. A signer number or recipientIndex alone is not enough
        // to bind a field to the participant in Acrobat Sign.
        var participantSetIds = await GetParticipantSetIdsByEmailAsync(
            baseUrl,
            agreementId,
            recipients,
            accessToken,
            cancellationToken);

        var fields = BuildFinancialRequestFormFields(
            anchorFields,
            recipients,
            participantSetIds);

        if (fields.Length == 0)
        {
            throw new InvalidOperationException(
                "No Adobe form fields were generated for the Financial Document Request template.");
        }

        // Adobe requires the ETag from GET /agreements/{id}/formFields
        // when updating the form-field collection.
        var etag = await GetAgreementFormFieldsEtagAsync(
            baseUrl,
            agreementId,
            accessToken,
            cancellationToken);

        var payload = JsonSerializer.Serialize(
            new { fields },
            JsonOptions);

        var url =
            $"{baseUrl}/agreements/{Uri.EscapeDataString(agreementId)}/formFields";

        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Put,
                    url);

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                request.Headers.TryAddWithoutValidation(
                    "If-Match",
                    etag);

                request.Content = new StringContent(
                    payload,
                    Encoding.UTF8,
                    "application/json");

                return Task.FromResult(request);
            },
            cancellationToken);

        var responseBody =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateAdobeException(
                "place the signer-specific fields on the Financial Document Request PDF",
                response.StatusCode,
                responseBody);
        }

        _logger.LogInformation(
            "Placed {FieldCount} Adobe Sign form fields on agreement {AgreementId}. Signer 1 ({Signer1Email}) is assigned to the Authorised Signatory column; signer 2 ({Signer2Email}) is assigned to the Recipient Acknowledgement column.",
            fields.Length,
            agreementId,
            recipients[0].Email,
            recipients.Count > 1 ? recipients[1].Email : "(not configured)");
    }

    private async Task<Dictionary<string, string>> GetParticipantSetIdsByEmailAsync(
        string baseUrl,
        string agreementId,
        List<AdobeSignRecipient> recipients,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url =
            $"{baseUrl}/agreements/{Uri.EscapeDataString(agreementId)}/members";

        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                return Task.FromResult(request);
            },
            cancellationToken);

        var responseBody =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateAdobeException(
                "retrieve the Adobe agreement participant sets for form-field assignment",
                response.StatusCode,
                responseBody);
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("participantSets", out var participantSetsElement) ||
            participantSetsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Adobe returned agreement members for {agreementId}, but no participantSets array was present. Response: {LimitErrorText(responseBody)}");
        }

        var participantSetIdsByEmail = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var participantSet in participantSetsElement.EnumerateArray())
        {
            var participantSetId = GetJsonString(participantSet, "id");

            if (string.IsNullOrWhiteSpace(participantSetId))
            {
                continue;
            }

            if (!participantSet.TryGetProperty("memberInfos", out var membersElement) ||
                membersElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var member in membersElement.EnumerateArray())
            {
                var email = GetJsonString(member, "email");

                if (!string.IsNullOrWhiteSpace(email))
                {
                    participantSetIdsByEmail[email.Trim()] = participantSetId;
                }
            }
        }

        // Match by email rather than assuming that a participant-set ID
        // can be calculated from signer order. Adobe IDs are generated IDs.
        for (var i = 0; i < recipients.Count; i++)
        {
            var email = recipients[i].Email.Trim();

            if (!participantSetIdsByEmail.TryGetValue(email, out var participantSetId) ||
                string.IsNullOrWhiteSpace(participantSetId))
            {
                throw new InvalidOperationException(
                    $"Adobe did not return a participant-set ID for signer {i + 1} ({email}). The form fields cannot safely be assigned. Check GET /agreements/{agreementId}/members before retrying.");
            }

            _logger.LogInformation(
                "Resolved Adobe participant set for signer {SignerNumber} ({SignerEmail}).",
                i + 1,
                email);
        }

        return participantSetIdsByEmail;
    }

    private async Task<string> GetAgreementFormFieldsEtagAsync(
        string baseUrl,
        string agreementId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        // Adobe documents GET /agreements/{id}/formFields as the
        // corresponding ETag source for PUT /agreements/{id}/formFields.
        var url =
            $"{baseUrl}/agreements/{Uri.EscapeDataString(agreementId)}/formFields";

        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    url);

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                return Task.FromResult(request);
            },
            cancellationToken);

        var responseBody =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateAdobeException(
                "retrieve the agreement form-fields ETag before updating form fields",
                response.StatusCode,
                responseBody);
        }

        var etag = response.Headers.ETag?.ToString();

        if (string.IsNullOrWhiteSpace(etag) &&
            response.Headers.TryGetValues("ETag", out var etagValues))
        {
            etag = etagValues.FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(etag))
        {
            throw new InvalidOperationException(
                $"Adobe did not return an ETag for the form fields on agreement {agreementId}. The v6 form-field update cannot safely continue without it.");
        }

        return etag;
    }

    private static object[] BuildFinancialRequestFormFields(
        List<AdobeSignAnchorField> anchorFields,
        List<AdobeSignRecipient> recipients,
        Dictionary<string, string> participantSetIdsByEmail)
    {
        ArgumentNullException.ThrowIfNull(anchorFields);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(participantSetIdsByEmail);

        var recipientCount = recipients.Count;

        if (recipientCount < 1 || recipientCount > 2)
        {
            throw new InvalidOperationException(
                "The Financial_Document_Request.pdf coordinate layout supports one or two recipients only.");
        }

        var requiredTypes = new[]
        {
            "SIGNATURE",
            "TEXT",
            "DATE"
        };

        for (var signerNumber = 1; signerNumber <= recipientCount; signerNumber++)
        {
            foreach (var requiredType in requiredTypes)
            {
                var matchCount = anchorFields.Count(field =>
                    field != null &&
                    field.SignerNumber == signerNumber &&
                    string.Equals(
                        field.InputType,
                        requiredType,
                        StringComparison.OrdinalIgnoreCase));

                if (matchCount != 1)
                {
                    throw new InvalidOperationException(
                        $"Signer {signerNumber} must have exactly one {requiredType} field configured. Found: {matchCount}.");
                }
            }
        }

        if (anchorFields.Any(field =>
                field == null ||
                field.SignerNumber < 1 ||
                field.SignerNumber > recipientCount))
        {
            throw new InvalidOperationException(
                "The anchor-field list contains a field assigned to a signer that is not in the recipient list.");
        }

        var result = new List<object>();

        foreach (var anchor in anchorFields.OrderBy(field => field.SignerNumber))
        {
            if (!string.IsNullOrWhiteSpace(anchor.InputType) &&
                !requiredTypes.Contains(
                    anchor.InputType,
                    StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The Financial_Document_Request.pdf layout does not define a position for field type '{anchor.InputType}'. Remove that optional field or configure a layout position for it.");
            }

            var recipient = recipients[anchor.SignerNumber - 1];
            var recipientEmail = recipient.Email.Trim();

            if (!participantSetIdsByEmail.TryGetValue(
                    recipientEmail,
                    out var participantSetId) ||
                string.IsNullOrWhiteSpace(participantSetId))
            {
                throw new InvalidOperationException(
                    $"No Adobe participant-set ID was resolved for signer {anchor.SignerNumber} ({recipientEmail}).");
            }

            var placement = GetFinancialRequestFieldPlacement(
                anchor.SignerNumber,
                anchor.InputType,
                anchor.Width,
                anchor.Height);

            var fieldName =
                $"signer_{anchor.SignerNumber}_{anchor.InputType.ToLowerInvariant()}";

            var locations = new[]
            {
                new
                {
                    pageNumber = 1,
                    top = placement.Top,
                    left = placement.Left,
                    width = placement.Width,
                    height = placement.Height
                }
            };

            object adobeField;

            if (string.Equals(
                    anchor.InputType,
                    "SIGNATURE",
                    StringComparison.OrdinalIgnoreCase))
            {
                adobeField = new
                {
                    name = fieldName,
                    inputType = "SIGNATURE",
                    contentType = "SIGNATURE",
                    required = anchor.Required,
                    assignee = participantSetId,
                    recipientIndex = anchor.SignerNumber,
                    visible = true,
                    locations
                };
            }
            else if (string.Equals(
                         anchor.InputType,
                         "TEXT",
                         StringComparison.OrdinalIgnoreCase))
            {
                // SIGNER_NAME is populated by Acrobat Sign from the
                // recipient assigned to this field.
                adobeField = new
                {
                    name = fieldName,
                    inputType = "TEXT_FIELD",
                    contentType = "SIGNER_NAME",
                    readOnly = true,
                    required = anchor.Required,
                    assignee = participantSetId,
                    recipientIndex = anchor.SignerNumber,
                    visible = true,
                    locations
                };
            }
            else
            {
                // SIGNATURE_DATE is populated by Acrobat Sign when this
                // recipient completes their signature action.
                adobeField = new
                {
                    name = fieldName,
                    inputType = "TEXT_FIELD",
                    contentType = "SIGNATURE_DATE",
                    displayFormatType = "DATE",
                    format = "DATE_MM_DD_YYYY",
                    readOnly = true,
                    required = anchor.Required,
                    assignee = participantSetId,
                    recipientIndex = anchor.SignerNumber,
                    visible = true,
                    locations
                };
            }

            result.Add(adobeField);
        }

        return result.ToArray();
    }

    private static (double Left, double Top, double Width, double Height)
        GetFinancialRequestFieldPlacement(
            int signerNumber,
            string inputType,
            int configuredWidth,
            int configuredHeight)
    {
        // Coordinates were measured from the supplied one-page
        // Financial_Document_Request.pdf (595.2756 x 841.8898 PDF points).
        // Acrobat Sign's FormFieldLocation uses Adobe-rendered pixels,
        // with Left measured from the page's left edge and Top measured
        // upward from the page's bottom edge. Adobe renders this A4 page
        // at approximately 992 x 1403 pixels, a 5/3 scale from PDF points.
        // The horizontal rules in PDF top-origin coordinates are at
        // y=675.52, y=713.52 and y=751.52 points respectively.
        var columnLeft = signerNumber switch
        {
            1 => 56.69d,
            2 => 316.91d,
            _ => throw new InvalidOperationException(
                "Only signer 1 and signer 2 have positions in this PDF template.")
        };

        var normalizedType = inputType.Trim().ToUpperInvariant();

        var (lineY, defaultWidth, defaultHeight) = normalizedType switch
        {
            "SIGNATURE" => (675.52d, 140d, 24d),
            "TEXT" => (713.52d, 120d, 20d),
            "DATE" => (751.52d, 120d, 20d),
            _ => throw new InvalidOperationException(
                $"No placement coordinates are configured for field type '{inputType}'.")
        };

        var width = configuredWidth > 0
            ? configuredWidth
            : defaultWidth;

        var height = configuredHeight > 0
            ? configuredHeight
            : defaultHeight;

        // Keep a small inset from the left edge of each signature line.
        // Place the field immediately above its corresponding line.
        // Convert PDF points to Adobe's rendered-pixel coordinate space.
        const double adobePixelsPerPdfPoint = 5d / 3d;
        const double pageHeightPoints = 841.8898d;
        const double leftInsetPoints = 4d;
        const double lineGapPoints = 4d;

        // Our measured positions use a top-left origin. Adobe's Top
        // property is measured upward from the bottom of the page to
        // the top edge of the field, so convert the origin and scale.
        var fieldTopFromPdfTopPoints =
            lineY - height - lineGapPoints;

        var fieldTopFromPdfBottomPoints =
            pageHeightPoints - fieldTopFromPdfTopPoints;

        var leftAdobePixels =
            (columnLeft + leftInsetPoints) * adobePixelsPerPdfPoint;

        var topAdobePixels =
            fieldTopFromPdfBottomPoints * adobePixelsPerPdfPoint;

        var widthAdobePixels = width * adobePixelsPerPdfPoint;
        var heightAdobePixels = height * adobePixelsPerPdfPoint;

        return (
            leftAdobePixels,
            topAdobePixels,
            widthAdobePixels,
            heightAdobePixels);
    }

    // ============================================================
    // 5. BUILD PARTICIPANT SETS
    // ============================================================

    private static object[] BuildParticipantSets(
        List<AdobeSignRecipient> recipients,
        string signingOrderMode)
    {
        var participantSets = new List<object>();

        for (var i = 0; i < recipients.Count; i++)
        {
            var recipient = recipients[i];

            var participantName = $"signer_{i + 1}";

            var order = string.Equals(
                signingOrderMode,
                "Parallel",
                StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : i + 1;

            participantSets.Add(new
            {
                memberInfos = new[]
                {
                    new
                    {
                        email = recipient.Email.Trim(),
                        name = recipient.Name.Trim()
                    }
                },

                order,
                role = "SIGNER",
                name = participantName
            });
        }

        return participantSets.ToArray();
    }

    // ============================================================
    // 6. UPLOAD TRANSIENT DOCUMENT
    // ============================================================

    private async Task<string> UploadTransientDocumentAsync(
        string baseUrl,
        byte[] pdfBytes,
        string fileName,
        string accessToken,
        CancellationToken cancellationToken)
    {
        // Do not automatically retry this POST. If Adobe processes an
        // upload but the response is lost, retrying can create another
        // transient document.

        using var response = await SendOnceAsync(
            () =>
            {
                var request = new HttpRequestMessage(
                    HttpMethod.Post,
                    $"{baseUrl}/transientDocuments");

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                var multipart = new MultipartFormDataContent();

                var fileContent = new ByteArrayContent(pdfBytes);

                fileContent.Headers.ContentType =
                    new MediaTypeHeaderValue("application/pdf");

                multipart.Add(fileContent, "File", fileName);
                multipart.Add(new StringContent(fileName), "File-Name");
                multipart.Add(new StringContent("application/pdf"), "Mime-Type");

                request.Content = multipart;

                return Task.FromResult(request);
            },
            cancellationToken);

        var responseBody =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateAdobeException(
                "upload the PDF to Adobe Sign",
                response.StatusCode,
                responseBody);
        }

        using var document = JsonDocument.Parse(responseBody);

        if (document.RootElement.TryGetProperty(
                "transientDocumentId",
                out var idElement))
        {
            return idElement.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException(
            "Adobe Sign did not return a transientDocumentId. Response: " +
            LimitErrorText(responseBody));
    }

    // ============================================================
    // 7. WAIT FOR AGREEMENT PROCESSING
    // ============================================================

    private async Task<string> WaitForAgreementReadyAsync(
        string baseUrl,
        string agreementId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        string? latestStatus = null;

        for (var attempt = 1; attempt <= MaxAgreementReadyAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            latestStatus = await GetAgreementStatusAsync(
                agreementId,
                accessToken,
                baseUrl,
                cancellationToken);

            if (string.Equals(
                    latestStatus,
                    "AUTHORING",
                    StringComparison.OrdinalIgnoreCase))
            {
                return latestStatus;
            }

            if (IsTerminalFailureStatus(latestStatus))
            {
                throw new InvalidOperationException(
                    $"Adobe agreement {agreementId} entered an unexpected state: {latestStatus}.");
            }

            _logger.LogInformation(
                "Adobe agreement {AgreementId} is still processing. Status: {Status}. Attempt: {Attempt}/{MaxAttempts}.",
                agreementId,
                latestStatus,
                attempt,
                MaxAgreementReadyAttempts);

            if (attempt < MaxAgreementReadyAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(2),
                    cancellationToken);
            }
        }

        throw new InvalidOperationException(
            $"Adobe agreement {agreementId} did not reach AUTHORING state within the expected time. Last status: {latestStatus ?? "Unknown"}.");
    }

    // ============================================================
    // 9. GET AGREEMENT STATUS
    // ============================================================

    public async Task<string> GetAgreementStatusAsync(
        string agreementId,
        string accessToken,
        string apiAccessPoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agreementId))
            throw new ArgumentException("An Adobe agreement ID is required.", nameof(agreementId));

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("An Adobe access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(apiAccessPoint))
            throw new ArgumentException("The Adobe Sign API access point is required.", nameof(apiAccessPoint));

        var baseUrl = NormalizeApiAccessPoint(apiAccessPoint);

        var url =
            $"{baseUrl}/agreements/{Uri.EscapeDataString(agreementId)}";

        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                return Task.FromResult(request);
            },
            cancellationToken);

        var responseBody =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateAdobeException(
                "retrieve the Adobe agreement status",
                response.StatusCode,
                responseBody);
        }

        using var document = JsonDocument.Parse(responseBody);

        var status = GetJsonString(document.RootElement, "status");

        if (string.IsNullOrWhiteSpace(status))
        {
            throw new InvalidOperationException(
                $"Adobe Sign did not return an agreement status for {agreementId}.");
        }

        return status.Trim().ToUpperInvariant();
    }


    // ============================================================
    // 10. GET LIVE RECIPIENT STATUSES
    // ============================================================

    /// <summary>
    /// Retrieves current participant-set statuses from Acrobat Sign.
    /// Each signer is represented by a participant set in this
    /// application, so the participant-set status is associated with
    /// the member email address.
    /// </summary>
    public async Task<List<AdobeSignRecipientStatus>> GetAgreementRecipientStatusesAsync(
        string agreementId,
        string accessToken,
        string apiAccessPoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agreementId))
            throw new ArgumentException("An Adobe agreement ID is required.", nameof(agreementId));

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("An Adobe access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(apiAccessPoint))
            throw new ArgumentException("The Adobe Sign API access point is required.", nameof(apiAccessPoint));

        var baseUrl = NormalizeApiAccessPoint(apiAccessPoint);
        var url =
            $"{baseUrl}/agreements/{Uri.EscapeDataString(agreementId.Trim())}/members";

        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                return Task.FromResult(request);
            },
            cancellationToken);

        var responseBody =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateAdobeException(
                "retrieve the Adobe agreement recipient statuses",
                response.StatusCode,
                responseBody);
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;

        if (!root.TryGetProperty("participantSets", out var participantSets) ||
            participantSets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Adobe did not return participantSets for agreement {agreementId}. " +
                LimitErrorText(responseBody));
        }

        var result = new List<AdobeSignRecipientStatus>();

        foreach (var participantSet in participantSets.EnumerateArray())
        {
            var participantSetStatus =
                GetJsonString(participantSet, "status") ?? "UNKNOWN";

            var order = 0;

            if (participantSet.TryGetProperty("order", out var orderElement) &&
                orderElement.TryGetInt32(out var parsedOrder))
            {
                order = parsedOrder;
            }

            if (!participantSet.TryGetProperty("memberInfos", out var members) ||
                members.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var member in members.EnumerateArray())
            {
                var email = GetJsonString(member, "email");

                if (string.IsNullOrWhiteSpace(email))
                {
                    continue;
                }

                var memberStatus =
                    GetJsonString(member, "status");

                var effectiveStatus =
                    !string.IsNullOrWhiteSpace(participantSetStatus) &&
                    !string.Equals(
                        participantSetStatus,
                        "UNKNOWN",
                        StringComparison.OrdinalIgnoreCase)
                        ? participantSetStatus
                        : memberStatus ?? "UNKNOWN";

                result.Add(new AdobeSignRecipientStatus(
                    GetJsonString(member, "name") ?? string.Empty,
                    email.Trim(),
                    effectiveStatus.Trim().ToUpperInvariant(),
                    order));
            }
        }

        return result
            .OrderBy(x => x.Order)
            .ThenBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ============================================================
    // 10. DOWNLOAD COMPLETED SIGNED PDF
    // ============================================================

    public async Task<byte[]> DownloadSignedDocumentAsync(
        string agreementId,
        string accessToken,
        string apiAccessPoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agreementId))
            throw new ArgumentException("An Adobe agreement ID is required.", nameof(agreementId));

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("An Adobe access token is required.", nameof(accessToken));

        if (string.IsNullOrWhiteSpace(apiAccessPoint))
            throw new ArgumentException("The Adobe Sign API access point is required.", nameof(apiAccessPoint));

        var baseUrl = NormalizeApiAccessPoint(apiAccessPoint);

        var url =
            $"{baseUrl}/agreements/{Uri.EscapeDataString(agreementId)}/combinedDocument";

        _logger.LogInformation(
            "Downloading combined signed PDF for Adobe agreement {AgreementId}.",
            agreementId);

        using var response = await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/pdf"));

                return Task.FromResult(request);
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody =
                await response.Content.ReadAsStringAsync(cancellationToken);

            throw CreateAdobeException(
                "download the signed Adobe agreement",
                response.StatusCode,
                errorBody);
        }

        var contentLength = response.Content.Headers.ContentLength;

        if (contentLength.HasValue && contentLength.Value > MaxPdfSizeBytes)
        {
            throw new InvalidOperationException(
                "The signed PDF returned by Adobe exceeds the maximum allowed size of 100 MB.");
        }

        await using var responseStream =
            await response.Content.ReadAsStreamAsync(cancellationToken);

        await using var memoryStream = new MemoryStream();

        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await responseStream.ReadAsync(
                   buffer.AsMemory(0, buffer.Length),
                   cancellationToken)) > 0)
        {
            if (memoryStream.Length + bytesRead > MaxPdfSizeBytes)
            {
                throw new InvalidOperationException(
                    "The signed PDF returned by Adobe exceeds the maximum allowed size of 100 MB.");
            }

            await memoryStream.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }

        var pdfBytes = memoryStream.ToArray();

        if (!IsPdfContent(pdfBytes))
        {
            throw new InvalidOperationException(
                $"Adobe Sign did not return a valid PDF for agreement {agreementId}.");
        }

        _logger.LogInformation(
            "Successfully downloaded signed PDF for agreement {AgreementId}. Size: {FileSize} bytes.",
            agreementId,
            pdfBytes.LongLength);

        return pdfBytes;
    }

    // ============================================================
    // 11. HTTP REQUEST HELPERS
    // ============================================================

    private async Task<HttpResponseMessage> SendJsonRequestAsync(
        HttpMethod method,
        string url,
        string json,
        string accessToken,
        CancellationToken cancellationToken)
    {
        return await SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(method, url);

                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", accessToken);

                request.Headers.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                request.Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json");

                return Task.FromResult(request);
            },
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        Func<Task<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(AdobeSignService));

        using var request = await requestFactory();

        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<Task<HttpRequestMessage>> requestFactory,
        CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(nameof(AdobeSignService));

        for (var attempt = 1; attempt <= MaxHttpRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = await requestFactory();

            HttpResponseMessage response;

            try
            {
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (HttpRequestException) when (attempt < MaxHttpRetries)
            {
                await Task.Delay(GetRetryDelay(attempt), cancellationToken);
                continue;
            }

            var shouldRetry =
                response.StatusCode == HttpStatusCode.TooManyRequests ||
                (int)response.StatusCode >= 500;

            if (!shouldRetry || attempt == MaxHttpRetries)
                return response;

            var retryDelay = GetRetryDelay(
                attempt,
                response.Headers.RetryAfter);

            _logger.LogWarning(
                "Adobe Sign returned HTTP {StatusCode}. Retrying in {RetryDelay} seconds. Attempt {Attempt}/{MaxAttempts}.",
                (int)response.StatusCode,
                retryDelay.TotalSeconds,
                attempt,
                MaxHttpRetries);

            response.Dispose();

            await Task.Delay(retryDelay, cancellationToken);
        }

        throw new InvalidOperationException(
            "Adobe Sign request could not be completed after the permitted retry attempts.");
    }

    private static TimeSpan GetRetryDelay(
        int attempt,
        RetryConditionHeaderValue? retryAfter = null)
    {
        if (retryAfter?.Delta != null)
            return ClampRetryDelay(retryAfter.Delta.Value);

        if (retryAfter?.Date != null)
        {
            var delay = retryAfter.Date.Value - DateTimeOffset.UtcNow;
            return ClampRetryDelay(delay);
        }

        var seconds = Math.Min(2 * Math.Pow(2, attempt - 1), 30);

        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan ClampRetryDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(1))
            return TimeSpan.FromSeconds(1);

        if (delay > TimeSpan.FromSeconds(60))
            return TimeSpan.FromSeconds(60);

        return delay;
    }

    // ============================================================
    // 12. AGREEMENT RESPONSE HELPERS
    // ============================================================

    private static string? ReadAgreementId(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);

        return GetJsonString(document.RootElement, "id");
    }

    private static string? GetJsonString(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    // ============================================================
    // 13. URL AND FILE VALIDATION
    // ============================================================

    private static string NormalizeApiAccessPoint(string apiAccessPoint)
    {
        if (!Uri.TryCreate(
                apiAccessPoint.Trim(),
                UriKind.Absolute,
                out var uri))
        {
            throw new InvalidOperationException(
                "The Adobe Sign API access point is not a valid absolute URL.");
        }

        if (!string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Adobe Sign API access point must use HTTPS.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "The Adobe Sign API access point contains an invalid URL component.");
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/') +
               $"/api/rest/{ApiVersion}";
    }

    private static void ValidateAdobeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "Adobe Sign returned an invalid authoring URL.");
        }

        if (!string.Equals(
                uri.Scheme,
                Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException(
                "Adobe Sign returned an authoring URL that is not a valid HTTPS URL.");
        }
    }

    private static string NormalizeFileName(string fileName)
    {
        var safeName = Path.GetFileName(fileName.Replace('\\', '/'));

        if (string.IsNullOrWhiteSpace(safeName))
            return "SignatureDocument.pdf";

        if (!string.Equals(
                Path.GetExtension(safeName),
                ".pdf",
                StringComparison.OrdinalIgnoreCase))
        {
            safeName = Path.ChangeExtension(safeName, ".pdf");
        }

        return safeName;
    }

    private static async Task<byte[]> ReadPdfAsync(
        Stream stream,
        int maximumSize,
        CancellationToken cancellationToken)
    {
        await using var memoryStream = new MemoryStream();

        var buffer = new byte[81920];
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(
                   buffer.AsMemory(0, buffer.Length),
                   cancellationToken)) > 0)
        {
            if (memoryStream.Length + bytesRead > maximumSize)
            {
                throw new InvalidOperationException(
                    $"The PDF exceeds the maximum allowed size of {maximumSize / (1024 * 1024)} MB.");
            }

            await memoryStream.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }

        var bytes = memoryStream.ToArray();

        if (!IsPdfContent(bytes))
        {
            throw new InvalidOperationException(
                "The uploaded document does not contain a valid PDF header.");
        }

        return bytes;
    }

    private static bool IsPdfContent(byte[]? bytes)
    {
        if (bytes == null || bytes.Length < 5)
            return false;

        var bytesToInspect = Math.Min(bytes.Length, 1028);

        return bytes
            .AsSpan(0, bytesToInspect)
            .IndexOf("%PDF-"u8) >= 0;
    }

    private static bool IsTerminalFailureStatus(string? status)
    {
        return status?.Trim().ToUpperInvariant() switch
        {
            "CANCELLED" => true,
            "CANCELED" => true,
            "EXPIRED" => true,
            "ABORTED" => true,
            "REJECTED" => true,
            "DECLINED" => true,
            "FAILED" => true,
            _ => false
        };
    }

    // ============================================================
    // 14. ADOBE ERROR HANDLING
    // ============================================================

    private static InvalidOperationException CreateAdobeException(
        string operation,
        HttpStatusCode statusCode,
        string responseBody)
    {
        var details = ExtractAdobeError(responseBody);

        return new InvalidOperationException(
            $"Unable to {operation}. Adobe Sign returned HTTP {(int)statusCode} ({statusCode}). {details}");
    }

    private static string ExtractAdobeError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
            return "Adobe Sign returned an empty error response.";

        try
        {
            using var document = JsonDocument.Parse(responseBody);

            var root = document.RootElement;

            var code = GetJsonString(root, "code");
            var message = GetJsonString(root, "message");
            var error = GetJsonString(root, "error");
            var description = GetJsonString(root, "description");

            var parts = new[]
            {
                code,
                message,
                error,
                description
            }.Where(value => !string.IsNullOrWhiteSpace(value));

            var result = string.Join(" - ", parts);

            return string.IsNullOrWhiteSpace(result)
                ? LimitErrorText(responseBody)
                : result;
        }
        catch (JsonException)
        {
            return LimitErrorText(responseBody);
        }
    }

    private static string LimitErrorText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "No additional error details were returned.";

        const int maximumLength = 1500;

        var cleaned = value.Trim();

        return cleaned.Length <= maximumLength
            ? cleaned
            : cleaned[..maximumLength] + "...";
    }
}

/// <summary>
/// Live recipient status returned by Acrobat Sign's agreement members API.
/// </summary>
public sealed record AdobeSignRecipientStatus(
    string Name,
    string Email,
    string Status,
    int Order);
