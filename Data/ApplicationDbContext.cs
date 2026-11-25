
using DocuWareAdobeSignIntegration.Models;
using Microsoft.EntityFrameworkCore;

namespace DocuWareAdobeSignIntegration.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(
        DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    // Database tables
    public DbSet<AdobeUserToken> AdobeUserTokens =>
        Set<AdobeUserToken>();

    public DbSet<AdobeAgreementWorkflow> AdobeAgreementWorkflows =>
        Set<AdobeAgreementWorkflow>();

    public DbSet<DocuWareUserToken> DocuWareUserTokens =>
        Set<DocuWareUserToken>();

    public DbSet<DashboardUserState> DashboardUserStates =>
        Set<DashboardUserState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ============================================
        // ADOBE USER TOKENS
        // ============================================

        modelBuilder.Entity<AdobeUserToken>(entity =>
        {
            entity.ToTable("AdobeUserTokens");

            entity.HasKey(x => x.UserId);

            entity.Property(x => x.UserId)
                .IsRequired()
                .HasMaxLength(450);

            entity.Property(x => x.EncryptedAccessToken)
                .IsRequired();

            entity.Property(x => x.EncryptedRefreshToken)
                .IsRequired();

            entity.Property(x => x.ApiAccessPoint)
                .IsRequired()
                .HasMaxLength(2048);
        });

        // ============================================
        // DOCUWARE USER TOKENS
        // ============================================

        modelBuilder.Entity<DocuWareUserToken>(entity =>
        {
            entity.ToTable("DocuWareUserTokens");

            entity.HasKey(x => x.UserId);

            entity.Property(x => x.UserId)
                .IsRequired()
                .HasMaxLength(450);

            entity.Property(x => x.EncryptedAccessToken)
                .IsRequired();

            entity.Property(x => x.EncryptedRefreshToken)
                .IsRequired();

            entity.Property(x => x.AccessTokenExpiresUtc)
                .IsRequired();

            entity.Property(x => x.UpdatedUtc)
                .IsRequired();
        });

        // ============================================
        // ADOBE AGREEMENT WORKFLOWS
        // ============================================

        modelBuilder.Entity<AdobeAgreementWorkflow>(entity =>
        {
            entity.ToTable("AdobeAgreementWorkflows");

            entity.HasKey(x => x.Id);

            // Agreement uniqueness
            entity.HasIndex(x => x.AgreementId)
                .IsUnique();

            // User workflow lookup
            entity.HasIndex(x => new
            {
                x.UserId,
                x.SentAtUtc
            });

            // Workflow status lookup
            entity.HasIndex(x => new
            {
                x.Status,
                x.SentAtUtc
            });

            // Archive processing lookup
            entity.HasIndex(x => new
            {
                x.ArchiveInProgress,
                x.LastArchiveAttemptUtc
            });

            // Completed documents / File Cabinet lookup
            entity.HasIndex(x => new
            {
                x.UserId,
                x.SignedPdfSavedAtUtc
            });

            // ========================================
            // AGREEMENT INFORMATION
            // ========================================

            entity.Property(x => x.AgreementId)
                .IsRequired()
                .HasMaxLength(128);

            entity.Property(x => x.UserId)
                .IsRequired()
                .HasMaxLength(450);

            entity.Property(x => x.SignerName)
                .HasMaxLength(255);

            entity.Property(x => x.SignerEmail)
                .IsRequired()
                .HasMaxLength(320);

            entity.Property(x => x.Subject)
                .HasMaxLength(500);

            // ========================================
            // MULTIPLE SIGNERS
            // ========================================

            entity.Property(x => x.RecipientsJson)
                .IsRequired()
                .HasDefaultValue("[]");

            entity.Property(x => x.SigningOrderMode)
                .IsRequired()
                .HasMaxLength(30)
                .HasDefaultValue("Sequential");

            // ========================================
            // ANCHOR-BASED SIGNATURE PLACEMENT
            // ========================================

            entity.Property(x => x.AnchorFieldsJson)
                .IsRequired()
                .HasDefaultValue("[]");

            // ========================================
            // ORIGINAL DOCUWARE DOCUMENT
            // ========================================

            entity.Property(x => x.OriginalDocumentId)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(x => x.FileCabinetId)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(x => x.SectionId)
                .HasMaxLength(100);

            entity.Property(x => x.FileName)
                .IsRequired()
                .HasMaxLength(255);

            // ========================================
            // WORKFLOW STATUS
            // ========================================

            entity.Property(x => x.Status)
                .IsRequired()
                .HasMaxLength(50);

            // ========================================
            // DOCUWARE ARCHIVE TRACKING
            // ========================================

            entity.Property(x => x.ArchivedDocumentId)
                .HasMaxLength(100);

            entity.Property(x => x.DocuWareArchiveStatus)
                .IsRequired()
                .HasMaxLength(50)
                .HasDefaultValue("Not saved");

            entity.Property(x => x.DocuWareArchiveError)
                .HasMaxLength(4000);

            // ========================================
            // SIGNED PDF STORAGE
            // ========================================

            entity.Property(x => x.SignedPdfPath)
                .HasMaxLength(2048);

            entity.Property(x => x.SignedPdfSizeBytes);

            // ========================================
            // FAILURE INFORMATION
            // ========================================

            entity.Property(x => x.FailureMessage)
                .HasMaxLength(4000);

            // ========================================
            // ARCHIVE PROCESSING
            // ========================================

            entity.Property(x => x.ArchiveInProgress)
                .IsRequired();

            entity.Property(x => x.ArchiveAttempts)
                .IsRequired();

            entity.Property(x => x.LastArchiveAttemptUtc);

            // ========================================
            // TIMESTAMPS
            // ========================================

            entity.Property(x => x.SentAtUtc)
                .IsRequired();

            entity.Property(x => x.CompletedAtUtc);

            entity.Property(x => x.ArchivedAtUtc);

            entity.Property(x => x.SignedPdfSavedAtUtc);

            entity.Property(x => x.UpdatedAtUtc)
                .IsRequired();
        });

        // ============================================
        // DASHBOARD USER STATE
        // ============================================

        modelBuilder.Entity<DashboardUserState>(entity =>
        {
            entity.ToTable("DashboardUserStates");

            entity.HasKey(x => x.UserId);

            entity.Property(x => x.UserId)
                .IsRequired()
                .HasMaxLength(450);

            entity.Property(x => x.DashboardClearedAtUtc);
        });
    }
}
