namespace DocuWareAdobeSignIntegration.Services
{
    public interface IAdobeAgreementArchiveService
    {
        /// <summary>
        /// Downloads a completed signed PDF from Adobe Sign and saves it
        /// in the application's local File Cabinet.
        /// </summary>
        Task MarkAgreementSignedAsync(
            string agreementId,
            string? notificationId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Checks the user's pending agreements and saves locally any
        /// agreements that Adobe Sign reports as signed.
        /// </summary>
        Task<int> SyncPendingAgreementsAsync(
            string userId,
            CancellationToken cancellationToken = default);
    }
}