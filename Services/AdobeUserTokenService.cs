
using System.Collections.Concurrent;

namespace DocuWareAdobeSignIntegration.Services;

public sealed class AdobeUserTokenService
{
    private readonly AdobeTokenStore _store;
    private readonly AdobeAuthService _authService;
    private readonly ILogger<AdobeUserTokenService> _logger;

    // Prevent simultaneous refresh requests for the same employee.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim>
        RefreshLocks = new();

    public AdobeUserTokenService(
        AdobeTokenStore store,
        AdobeAuthService authService,
        ILogger<AdobeUserTokenService> logger)
    {
        _store = store;
        _authService = authService;
        _logger = logger;
    }

    // ======================================================
    // Get Valid Adobe Sign Access Token
    // ======================================================

    public async Task<AdobeStoredToken> GetValidTokenAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        var token = await _store.GetAsync(
            userId,
            cancellationToken);

        if (token == null)
        {
            throw new InvalidOperationException(
                "You have not connected your Adobe Sign account. " +
                "Please connect Adobe Sign first.");
        }

        // Return the existing token if it is still valid.
        if (token.AccessTokenExpiresUtc >
            DateTime.UtcNow.AddMinutes(5))
        {
            return token;
        }

        // ==================================================
        // Acquire Per-Employee Refresh Lock
        // ==================================================

        var refreshLock = RefreshLocks.GetOrAdd(
            userId,
            _ => new SemaphoreSlim(1, 1));

        await refreshLock.WaitAsync(cancellationToken);

        try
        {
            // Another request may already have refreshed the token.
            token = await _store.GetAsync(
                userId,
                cancellationToken);

            if (token == null)
            {
                throw new InvalidOperationException(
                    "The Adobe Sign connection could not be retrieved.");
            }

            if (token.AccessTokenExpiresUtc >
                DateTime.UtcNow.AddMinutes(5))
            {
                return token;
            }

            // ==================================================
            // Refresh Adobe Sign Access Token
            // ==================================================

            _logger.LogInformation(
                "Refreshing Adobe Sign access token for employee {UserId}.",
                userId);

            var refreshed = await _authService.RefreshTokenAsync(
                token.RefreshToken,
                token.ApiAccessPoint,
                cancellationToken);

            if (refreshed == null
                || string.IsNullOrWhiteSpace(refreshed.AccessToken))
            {
                throw new InvalidOperationException(
                    "Adobe Sign returned an invalid refreshed access token.");
            }

            // ==================================================
            // Save Refreshed Tokens
            // ==================================================

            await _store.SaveAsync(
                userId,
                refreshed,
                token.ApiAccessPoint,
                cancellationToken);

            // ==================================================
            // Retrieve Updated Stored Token
            // ==================================================

            token = await _store.GetAsync(
                userId,
                cancellationToken);

            if (token == null)
            {
                throw new InvalidOperationException(
                    "Unable to retrieve the refreshed Adobe Sign connection.");
            }

            _logger.LogInformation(
                "Adobe Sign access token refreshed successfully for employee {UserId}.",
                userId);

            return token;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Adobe Sign token refresh failed for employee {UserId}.",
                userId);

            throw;
        }
        finally
        {
            refreshLock.Release();
        }
    }
}
