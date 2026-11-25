
using System.Security.Cryptography;
using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DocuWareAdobeSignIntegration.Services;

public sealed class DocuWareTokenStore
{
    private readonly ApplicationDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<DocuWareTokenStore> _logger;

    public DocuWareTokenStore(
        ApplicationDbContext db,
        IDataProtectionProvider protectionProvider,
        ILogger<DocuWareTokenStore> logger)
    {
        _db = db;
        _logger = logger;

        // Dedicated encryption purpose for DocuWare tokens.
        _protector = protectionProvider.CreateProtector(
            "DocuWareAdobeSignIntegration.DocuWareTokens.v1");
    }

    // ======================================================
    // Save DocuWare OAuth Tokens
    // ======================================================

    public async Task SaveAsync(
        string userId,
        string accessToken,
        string refreshToken,
        DateTime expiresUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ArgumentException(
                "DocuWare access token is required.",
                nameof(accessToken));
        }

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException(
                "DocuWare did not return a refresh token. " +
                "Check the offline_access scope and App Registration settings.");
        }

        if (expiresUtc == default)
        {
            throw new ArgumentException(
                "A valid token expiration date is required.",
                nameof(expiresUtc));
        }

        var record = await _db.DocuWareUserTokens
            .SingleOrDefaultAsync(
                x => x.UserId == userId,
                cancellationToken);

        if (record == null)
        {
            record = new DocuWareUserToken
            {
                UserId = userId
            };

            _db.DocuWareUserTokens.Add(record);
        }

        // Encrypt tokens before storing them in SQLite.
        record.EncryptedAccessToken =
            _protector.Protect(accessToken);

        record.EncryptedRefreshToken =
            _protector.Protect(refreshToken);

        record.AccessTokenExpiresUtc =
            expiresUtc.Kind == DateTimeKind.Utc
                ? expiresUtc
                : expiresUtc.ToUniversalTime();

        record.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "DocuWare OAuth tokens securely stored for employee {UserId}.",
            userId);
    }

    // ======================================================
    // Retrieve and Decrypt DocuWare OAuth Tokens
    // ======================================================

    public async Task<DocuWareStoredToken?> GetAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        var record = await _db.DocuWareUserTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(
                x => x.UserId == userId,
                cancellationToken);

        if (record == null)
        {
            return null;
        }

        try
        {
            var accessToken =
                _protector.Unprotect(
                    record.EncryptedAccessToken);

            var refreshToken =
                _protector.Unprotect(
                    record.EncryptedRefreshToken);

            if (string.IsNullOrWhiteSpace(accessToken)
                || string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new InvalidOperationException(
                    "The stored DocuWare token values are empty.");
            }

            return new DocuWareStoredToken(
                record.UserId,
                accessToken,
                refreshToken,
                DateTime.SpecifyKind(
                    record.AccessTokenExpiresUtc,
                    DateTimeKind.Utc));
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(
                ex,
                "Unable to decrypt DocuWare OAuth tokens for employee {UserId}.",
                userId);

            throw new InvalidOperationException(
                "The stored DocuWare tokens could not be decrypted. " +
                "Check that the ASP.NET Core Data Protection keys have been preserved.",
                ex);
        }
    }

    // ======================================================
    // Delete DocuWare OAuth Tokens
    // ======================================================

    public async Task DeleteAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        var record = await _db.DocuWareUserTokens
            .SingleOrDefaultAsync(
                x => x.UserId == userId,
                cancellationToken);

        if (record == null)
        {
            return;
        }

        _db.DocuWareUserTokens.Remove(record);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "DocuWare OAuth tokens deleted for employee {UserId}.",
            userId);
    }
}

// ==========================================================
// Decrypted DocuWare Token Result
// ==========================================================

public sealed record DocuWareStoredToken(
    string UserId,
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresUtc);
