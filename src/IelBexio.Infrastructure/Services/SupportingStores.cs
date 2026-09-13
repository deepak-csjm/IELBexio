using IelBexio.Ai.Gateway;
using IelBexio.Application.Abstractions;
using IelBexio.Connectors.Bexio.Auth;
using IelBexio.Domain.Ai;
using IelBexio.Domain.Bexio;
using IelBexio.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IelBexio.Infrastructure.Services;

/// <summary>
/// Encrypts tokens at rest using ASP.NET Core Data Protection.
/// <para>
/// The purpose is narrow and worth being precise about: it stops a database dump, a log of a row, or a
/// backup from yielding usable Bexio credentials. It does not protect against an attacker who already
/// has both the database and the key ring. In Azure the key ring is persisted to Blob Storage and
/// encrypted with Key Vault, so those are separate trust boundaries; locally the keys sit on disk,
/// which is stated plainly in docs/security.md rather than overclaimed.
/// </para>
/// </summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        // A purpose string binds the ciphertext to this use: a token protected here cannot be
        // unprotected by a protector created for another purpose.
        _protector = provider.CreateProtector("IelBexio.BexioTokens.v1");
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Almost always a rotated or lost key ring. Returning null makes the caller treat it as
            // "reconnect required" rather than crashing a background worker.
            return null;
        }
    }
}

/// <summary>Stores the tenant's Bexio connection.</summary>
public sealed class BexioConnectionStore : IBexioConnectionStore
{
    private readonly AppDbContext _db;
    private readonly ITenantContext _tenant;

    public BexioConnectionStore(AppDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public Task<BexioConnection?> GetAsync(CancellationToken cancellationToken = default) =>
        _db.BexioConnections.FirstOrDefaultAsync(cancellationToken);

    public async Task SaveAsync(BexioConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection.TenantId == Guid.Empty)
        {
            connection.TenantId = _tenant.TenantId;
        }

        if (_db.Entry(connection).State == EntityState.Detached)
        {
            _db.BexioConnections.Add(connection);
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        var connection = await GetAsync(cancellationToken);
        if (connection is not null)
        {
            _db.BexioConnections.Remove(connection);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}

/// <summary>
/// Database-backed AI usage ledger: budget accounting, per-invoice call counting, and the extraction
/// cache of §13.
/// </summary>
public sealed class AiUsageLedger : IAiUsageLedger
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;
    private readonly ILogger<AiUsageLedger> _logger;

    public AiUsageLedger(AppDbContext db, IClock clock, ILogger<AiUsageLedger> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public Task<int> CountCallsForInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken = default) =>
        _db.AiUsageRecords.CountAsync(r => r.InvoiceId == invoiceId && !r.ServedFromCache, cancellationToken);

    public async Task<decimal> GetMonthToDateSpendAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

        return await _db.AiUsageRecords
            .Where(r => r.OccurredAt >= monthStart)
            .SumAsync(r => (decimal?)r.EstimatedCost, cancellationToken) ?? 0m;
    }

    public async Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        _db.AiUsageRecords.Add(record);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a cached extraction. The cache lives in <c>extraction_results</c> rather than in memory so
    /// it survives restarts and is visible to an operator — an invisible cache that silently serves a
    /// stale extraction is hard to debug when a document is re-processed and nothing changes.
    /// </summary>
    public async Task<string?> TryGetCachedAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        var cached = await _db.ExtractionResults
            .Where(r => r.CacheKey == cacheKey && r.Succeeded)
            .OrderByDescending(r => r.ProducedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return cached?.ExtractedJson;
    }

    public async Task CacheAsync(string cacheKey, string response, TimeSpan lifetime, CancellationToken cancellationToken = default)
    {
        var existing = await _db.ExtractionResults.FirstOrDefaultAsync(r => r.CacheKey == cacheKey, cancellationToken);

        if (existing is not null)
        {
            existing.ExtractedJson = response;
            existing.ProducedAt = _clock.UtcNow;
        }
        else
        {
            _db.ExtractionResults.Add(new ExtractionResultCacheEntry(cacheKey, response, _clock.UtcNow).ToEntity());
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A cache write losing a race is not worth failing the caller's operation over.
            _logger.LogDebug(ex, "Could not write the AI extraction cache entry for {CacheKey}.", cacheKey);
        }
    }

    private sealed record ExtractionResultCacheEntry(string CacheKey, string Json, DateTimeOffset Now)
    {
        public Domain.Documents.ExtractionResult ToEntity() => new()
        {
            CacheKey = CacheKey,
            ExtractedJson = Json,
            Succeeded = true,
            ProducedAt = Now,
            Method = Domain.Documents.ExtractionMethod.AiExtraction,
            ExtractorVersion = AiPromptLibrary.Version,
        };
    }
}
