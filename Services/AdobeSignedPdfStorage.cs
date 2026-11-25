using System.Security.Cryptography;
using System.Text;

namespace DocuWareAdobeSignIntegration.Services;

public interface IAdobeSignedPdfStorage
{
    Task<string> SaveAsync(
        string userId,
        string agreementId,
        byte[] pdf,
        CancellationToken cancellationToken = default);

    FileStream OpenRead(string relativePath);

    string GetAbsolutePath(string relativePath);
}

public sealed class AdobeSignedPdfStorage : IAdobeSignedPdfStorage
{
    private const long MaxFileSizeBytes = 100L * 1024 * 1024;

    private readonly string _rootPath;
    private readonly ILogger<AdobeSignedPdfStorage> _logger;

    public AdobeSignedPdfStorage(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<AdobeSignedPdfStorage> logger)
    {
        _logger = logger;

        var configuredPath =
            configuration["FileCabinet:StoragePath"];

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            configuredPath = Path.Combine(
                "App_Data",
                "SignedDocuments");
        }

        _rootPath = Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(
                    environment.ContentRootPath,
                    configuredPath));

        Directory.CreateDirectory(_rootPath);

        _logger.LogInformation(
            "Signed PDF storage configured at {StoragePath}.",
            _rootPath);
    }

    public async Task<string> SaveAsync(
        string userId,
        string agreementId,
        byte[] pdf,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException(
                "User ID is required.",
                nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(agreementId))
        {
            throw new ArgumentException(
                "Agreement ID is required.",
                nameof(agreementId));
        }

        if (pdf == null || pdf.Length == 0)
        {
            throw new InvalidDataException(
                "The signed PDF is empty.");
        }

        if (pdf.Length > MaxFileSizeBytes)
        {
            throw new InvalidOperationException(
                "The signed PDF exceeds the 100 MB limit.");
        }

        ValidatePdf(pdf);

        // Never use user-supplied IDs as filesystem directory names.
        var userFolder = Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(userId)));

        var directory = Path.Combine(
            _rootPath,
            userFolder);

        Directory.CreateDirectory(directory);

        // Generate a unique filename rather than trusting the agreement ID.
        var generatedName = $"{Guid.NewGuid():N}.pdf";

        var absolutePath = Path.Combine(
            directory,
            generatedName);

        await using (var output = new FileStream(
            absolutePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await output.WriteAsync(
                pdf,
                cancellationToken);

            await output.FlushAsync(cancellationToken);
        }

        // Store only a relative path in the database.
        var relativePath = Path.GetRelativePath(
            _rootPath,
            absolutePath);

        _logger.LogInformation(
            "Stored signed PDF for agreement {AgreementId}. Size: {SizeBytes} bytes.",
            agreementId,
            pdf.Length);

        return relativePath;
    }

    public FileStream OpenRead(string relativePath)
    {
        var absolutePath = GetAbsolutePath(relativePath);

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException(
                "The signed PDF could not be found.");
        }

        return new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public string GetAbsolutePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                "Invalid stored PDF path.");
        }

        var fullPath = Path.GetFullPath(
            Path.Combine(
                _rootPath,
                relativePath));

        var rootWithSeparator =
            _rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(
            rootWithSeparator,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The stored PDF path is outside the File Cabinet storage directory.");
        }

        return fullPath;
    }

    private static void ValidatePdf(byte[] pdf)
    {
        if (pdf.Length < 5 ||
            pdf[0] != 0x25 ||
            pdf[1] != 0x50 ||
            pdf[2] != 0x44 ||
            pdf[3] != 0x46 ||
            pdf[4] != 0x2D)
        {
            throw new InvalidDataException(
                "Adobe Sign did not return a valid PDF file.");
        }
    }
}