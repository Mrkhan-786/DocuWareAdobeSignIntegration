
using System.Security.Cryptography;
using DocuWareAdobeSignIntegration.Data;
using DocuWareAdobeSignIntegration.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace DocuWareAdobeSignIntegration.Services;

public sealed class AdobeTokenStore
{
    private readonly ApplicationDbContext _db;
    private readonly IDataProtector _protector;
    private readonly ILogger<AdobeTokenStore> _logger;

    public AdobeTokenStore(
        ApplicationDbContext db,
        IDataProtectionProvider protectionProvider,
        ILogger<AdobeTokenStore> logger)
    {
        _db = db;
        _logger = logger;

        // Separate encryption purpose from DocuWare tokens.
        _protector = protectionProvider.CreateProtector(
            "DocuWareAdobeSignIntegration.AdobeTokens.v1");
    }

    // ======================================================
    // Save Adobe Sign Tokens
    // ======================================================

    public async Task SaveAsync(
        string userId,
        AdobeTokenResponse token,
        string apiAccessPoint,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        ArgumentNullException.ThrowIfNull(token);

        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new ArgumentException(
                "Adobe access token is required.",
                nameof(token));
        }

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            throw new InvalidOperationException(
                "Adobe Sign did not return a refresh token.");
        }

        if (!Uri.TryCreate(
                apiAccessPoint,
                UriKind.Absolute,
                out var apiUri)
            || apiUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(
                "Adobe API access point must be a valid HTTPS URL.");
        }

        var record = await _db.AdobeUserTokens
            .SingleOrDefaultAsync(
                x => x.UserId == userId,
                cancellationToken);

        if (record == null)
        {
            record = new AdobeUserToken
            {
                UserId = userId
            };

            _db.AdobeUserTokens.Add(record);
        }

        // Encrypt tokens before database storage.
        record.EncryptedAccessToken =
            _protector.Protect(token.AccessToken);

        record.EncryptedRefreshToken =
            _protector.Protect(token.RefreshToken);

        record.ApiAccessPoint =
            apiUri.ToString().TrimEnd('/');

        record.AccessTokenExpiresUtc =
            DateTime.UtcNow.AddSeconds(
                Math.Max(token.ExpiresIn, 60));

        record.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Adobe Sign tokens securely saved for employee {UserId}.",
            userId);
    }

    // ======================================================
    // Retrieve and Decrypt Adobe Sign Tokens
    // ======================================================

    public async Task<AdobeStoredToken?> GetAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        var record = await _db.AdobeUserTokens
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
            if (!Uri.TryCreate(
                    record.ApiAccessPoint,
                    UriKind.Absolute,
                    out var apiUri)
                || apiUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException(
                    "The stored Adobe API access point is invalid.");
            }

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
                    "The stored Adobe Sign token values are empty.");
            }

            return new AdobeStoredToken(
                record.UserId,
                accessToken,
                refreshToken,
                apiUri.ToString().TrimEnd('/'),
                DateTime.SpecifyKind(
                    record.AccessTokenExpiresUtc,
                    DateTimeKind.Utc));
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(
                ex,
                "Unable to decrypt Adobe Sign tokens for employee {UserId}.",
                userId);

            throw new InvalidOperationException(
                "The stored Adobe Sign tokens could not be decrypted. " +
                "Check that the ASP.NET Core Data Protection keys have been preserved.",
                ex);
        }
    }

    // ======================================================
    // Delete Adobe Sign Tokens
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

        var record = await _db.AdobeUserTokens
            .SingleOrDefaultAsync(
                x => x.UserId == userId,
                cancellationToken);

        if (record == null)
        {
            return;
        }

        _db.AdobeUserTokens.Remove(record);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Adobe Sign tokens deleted for employee {UserId}.",
            userId);
    }
}

// ==========================================================
// Decrypted Adobe Sign Token Result
// ==========================================================

public sealed record AdobeStoredToken(
    string UserId,
    string AccessToken,
    string RefreshToken,
    string ApiAccessPoint,
    DateTime AccessTokenExpiresUtc);
