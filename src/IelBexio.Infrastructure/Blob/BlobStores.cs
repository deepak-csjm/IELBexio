using System.Globalization;
using IelBexio.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IelBexio.Infrastructure.Blob;

public sealed class BlobStorageOptions
{
    public const string SectionName = "BlobStorage";

    /// <summary>"Local" or "Azure". Local keeps the POC runnable with no Azure dependency (§33).</summary>
    public string Provider { get; set; } = "Local";

    /// <summary>Root directory for the local provider. Must not be inside wwwroot — these files are private.</summary>
    public string LocalRootPath { get; set; } = "local-blobs";

    public string? AzureConnectionString { get; set; }
    public string DefaultContainer { get; set; } = "documents";
}

/// <summary>
/// Filesystem-backed private blob store for local development and tests.
/// <para>
/// Path safety: the caller-supplied blob name is never trusted. Every key is re-derived from a GUID
/// and a sanitised extension, and the resolved path is asserted to stay under the configured root, so
/// a name like <c>../../etc/passwd</c> cannot escape (§25 path traversal).
/// </para>
/// </summary>
public sealed class LocalFileBlobStore : IBlobStore
{
    private readonly string _root;
    private readonly ILogger<LocalFileBlobStore> _logger;

    public LocalFileBlobStore(IOptions<BlobStorageOptions> options, ILogger<LocalFileBlobStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _root = Path.GetFullPath(options.Value.LocalRootPath);
        _logger = logger;
        Directory.CreateDirectory(_root);
    }

    public async Task<string> PutAsync(string container, string blobName, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var safeContainer = Sanitize(container);
        var safeName = Sanitize(blobName);
        var directory = Path.Combine(_root, safeContainer);
        Directory.CreateDirectory(directory);

        var path = Path.GetFullPath(Path.Combine(directory, safeName));
        AssertWithinRoot(path);

        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        _logger.LogInformation("Stored blob {Container}/{Name} ({ContentType})", safeContainer, safeName, contentType);
        return $"local://{safeContainer}/{safeName}";
    }

    public Task<Stream?> GetAsync(string blobUri, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(blobUri);
        if (path is null || !File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        return Task.FromResult<Stream?>(File.OpenRead(path));
    }

    public Task<bool> ExistsAsync(string blobUri, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(blobUri);
        return Task.FromResult(path is not null && File.Exists(path));
    }

    public Task DeleteAsync(string blobUri, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(blobUri);
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The local provider issues no URL at all. Documents are streamed through an authorised
    /// application endpoint instead, which is the correct behaviour: there is never a public URL (§25).
    /// </summary>
    public Task<Uri?> CreateReadUrlAsync(string blobUri, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);

    private string? ResolvePath(string blobUri)
    {
        if (string.IsNullOrWhiteSpace(blobUri) || !blobUri.StartsWith("local://", StringComparison.Ordinal))
        {
            return null;
        }

        var relative = blobUri["local://".Length..];
        var parts = relative.Split('/', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        var path = Path.GetFullPath(Path.Combine(_root, Sanitize(parts[0]), Sanitize(parts[1])));
        AssertWithinRoot(path);
        return path;
    }

    private void AssertWithinRoot(string fullPath)
    {
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Blob path resolves outside the configured storage root.");
        }
    }

    /// <summary>Strips everything that is not a safe filename character. Rejects traversal outright.</summary>
    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unnamed";
        }

        var cleaned = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.').ToArray());
        cleaned = cleaned.Replace("..", ".", StringComparison.Ordinal).Trim('.');
        return string.IsNullOrEmpty(cleaned)
            ? Guid.CreateVersion7().ToString("n")
            : cleaned;
    }
}

/// <summary>
/// Azure Blob Storage implementation. Containers are private; browser access, where needed at all, is
/// via a short-lived SAS created on demand and never persisted.
/// </summary>
public sealed class AzureBlobStore : IBlobStore
{
    private readonly global::Azure.Storage.Blobs.BlobServiceClient _client;
    private readonly ILogger<AzureBlobStore> _logger;

    public AzureBlobStore(IOptions<BlobStorageOptions> options, ILogger<AzureBlobStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connectionString = options.Value.AzureConnectionString
            ?? throw new InvalidOperationException("BlobStorage:AzureConnectionString is required when Provider=Azure.");
        _client = new global::Azure.Storage.Blobs.BlobServiceClient(connectionString);
        _logger = logger;
    }

    public async Task<string> PutAsync(string container, string blobName, Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        var containerClient = _client.GetBlobContainerClient(container);

        // PublicAccessType.None: no anonymous read, ever.
        await containerClient.CreateIfNotExistsAsync(global::Azure.Storage.Blobs.Models.PublicAccessType.None, cancellationToken: cancellationToken);

        var blob = containerClient.GetBlobClient(blobName);
        await blob.UploadAsync(
            content,
            new global::Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType },
            cancellationToken: cancellationToken);

        _logger.LogInformation("Uploaded blob {Container}/{Name}", container, blobName);
        return blob.Uri.ToString();
    }

    public async Task<Stream?> GetAsync(string blobUri, CancellationToken cancellationToken = default)
    {
        var blob = new global::Azure.Storage.Blobs.BlobClient(new Uri(blobUri));
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return response.Value.Content;
    }

    public async Task<bool> ExistsAsync(string blobUri, CancellationToken cancellationToken = default) =>
        await new global::Azure.Storage.Blobs.BlobClient(new Uri(blobUri)).ExistsAsync(cancellationToken);

    public async Task DeleteAsync(string blobUri, CancellationToken cancellationToken = default) =>
        await new global::Azure.Storage.Blobs.BlobClient(new Uri(blobUri)).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    public Task<Uri?> CreateReadUrlAsync(string blobUri, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var blob = new global::Azure.Storage.Blobs.BlobClient(new Uri(blobUri));
        if (!blob.CanGenerateSasUri)
        {
            // With managed identity there is no account key to sign with; a user-delegation key would
            // be required. Returning null makes the caller fall back to streaming through the app.
            return Task.FromResult<Uri?>(null);
        }

        var uri = blob.GenerateSasUri(
            global::Azure.Storage.Sas.BlobSasPermissions.Read,
            DateTimeOffset.UtcNow.Add(lifetime));

        return Task.FromResult<Uri?>(uri);
    }
}

/// <summary>Generates safe, non-guessable blob names. Never derived from the uploaded filename.</summary>
public static class BlobNaming
{
    public static string Create(DateTimeOffset now, string? originalFileName)
    {
        var extension = Path.GetExtension(originalFileName ?? string.Empty);
        var safeExtension = new string(extension.Where(c => char.IsAsciiLetterOrDigit(c) || c == '.').Take(12).ToArray());
        if (!safeExtension.StartsWith('.'))
        {
            safeExtension = string.IsNullOrEmpty(safeExtension) ? ".bin" : "." + safeExtension;
        }

        var datePart = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return $"{datePart}-{Guid.CreateVersion7():n}{safeExtension}";
    }
}
