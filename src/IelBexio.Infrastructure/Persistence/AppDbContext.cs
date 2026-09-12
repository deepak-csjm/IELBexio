using IelBexio.Application.Abstractions;
using IelBexio.Domain.Ai;
using IelBexio.Domain.Audit;
using IelBexio.Domain.Bexio;
using IelBexio.Domain.Common;
using IelBexio.Domain.Documents;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Mapping;
using IelBexio.Domain.Sync;
using IelBexio.Domain.Tax;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace IelBexio.Infrastructure.Persistence;

/// <summary>
/// The application's PostgreSQL context. PostgreSQL is the internal source of truth for workflow
/// state, normalised records, mappings, provenance, approvals and synchronisation state (§0, §26).
/// </summary>
public sealed class AppDbContext : DbContext
{
    private readonly ITenantContext? _tenantContext;
    private readonly IClock _clock;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext? tenantContext = null, IClock? clock = null)
        : base(options)
    {
        _tenantContext = tenantContext;
        _clock = clock ?? new SystemClock();
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<SourceConnection> SourceConnections => Set<SourceConnection>();
    public DbSet<ImportRun> ImportRuns => Set<ImportRun>();
    public DbSet<RawSourcePayload> RawSourcePayloads => Set<RawSourcePayload>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<TaxAssessment> TaxAssessments => Set<TaxAssessment>();
    public DbSet<DocumentArtifact> DocumentArtifacts => Set<DocumentArtifact>();
    public DbSet<ExtractionResult> ExtractionResults => Set<ExtractionResult>();
    public DbSet<AiProposal> AiProposals => Set<AiProposal>();
    public DbSet<AiUsageRecord> AiUsageRecords => Set<AiUsageRecord>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<SynchronizationAttempt> SynchronizationAttempts => Set<SynchronizationAttempt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<FieldProvenance> FieldProvenances => Set<FieldProvenance>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<CustomerMapping> CustomerMappings => Set<CustomerMapping>();
    public DbSet<ProductMapping> ProductMappings => Set<ProductMapping>();
    public DbSet<TaxMapping> TaxMappings => Set<TaxMapping>();
    public DbSet<AccountMapping> AccountMappings => Set<AccountMapping>();
    public DbSet<BexioConnection> BexioConnections => Set<BexioConnection>();
    public DbSet<BexioReferenceItem> BexioReferenceItems => Set<BexioReferenceItem>();

    /// <summary>
    /// Tenant currently in scope. Global query filters compare against this, so a query that forgets
    /// to filter by tenant still cannot see another tenant's rows (§23).
    /// </summary>
    public Guid CurrentTenantId => _tenantContext?.IsResolved == true ? _tenantContext.TenantId : Guid.Empty;

    /// <summary>
    /// Set to true only by maintenance paths that legitimately span tenants (migrations, the
    /// reconciliation sweep, admin tooling). Never set from a request-scoped path.
    /// </summary>
    public bool SuppressTenantFilter { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        ApplyTenantQueryFilters(modelBuilder);

        // Applied last so it also renames anything the configurations named explicitly.
        modelBuilder.ApplySnakeCaseNames();

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Applies a tenant predicate to every <see cref="TenantEntity"/> automatically. Doing this by
    /// reflection rather than per-configuration is deliberate: a new entity is isolated the moment it
    /// is added, with no chance of a developer forgetting the filter. The filter closes over
    /// <c>this</c>, so EF re-evaluates <see cref="CurrentTenantId"/> on every query.
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(TenantEntity).IsAssignableFrom(entityType.ClrType) || entityType.BaseType is not null)
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var tenantProperty = Expression.Call(
                typeof(EF),
                nameof(EF.Property),
                [typeof(Guid)],
                parameter,
                Expression.Constant(nameof(TenantEntity.TenantId)));

            var contextConstant = Expression.Constant(this);
            var currentTenant = Expression.Property(contextConstant, nameof(CurrentTenantId));
            var suppress = Expression.Property(contextConstant, nameof(SuppressTenantFilter));

            // e.TenantId == CurrentTenantId || SuppressTenantFilter
            var body = Expression.OrElse(Expression.Equal(tenantProperty, currentTenant), suppress);

            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    public override int SaveChanges()
    {
        ApplyConventions();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyConventions();
        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Stamps timestamps and, critically, the tenant id. Assigning <c>TenantId</c> centrally means a
    /// developer cannot create a business row without one, and cannot create one for another tenant.
    /// </summary>
    private void ApplyConventions()
    {
        var now = _clock.UtcNow;

        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity.CreatedAt == default)
                    {
                        entry.Entity.CreatedAt = now;
                    }

                    entry.Entity.UpdatedAt = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    break;
                default:
                    break;
            }
        }

        if (CurrentTenantId == Guid.Empty)
        {
            return;
        }

        foreach (var entry in ChangeTracker.Entries<TenantEntity>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TenantId == Guid.Empty)
            {
                entry.Entity.TenantId = CurrentTenantId;
            }
            else if (entry.State is EntityState.Modified or EntityState.Deleted
                     && entry.Entity.TenantId != CurrentTenantId
                     && !SuppressTenantFilter)
            {
                throw new CrossTenantAccessException(entry.Entity.GetType().Name, entry.Entity.TenantId, CurrentTenantId);
            }
        }
    }
}

/// <summary>Raised when a write would cross a tenant boundary. Always a bug, never an expected outcome.</summary>
public sealed class CrossTenantAccessException : InvalidOperationException
{
    public CrossTenantAccessException(string entityType, Guid entityTenantId, Guid currentTenantId)
        : base($"Refusing to modify {entityType} belonging to tenant {entityTenantId:n} while operating as tenant {currentTenantId:n}.")
    {
    }

    public CrossTenantAccessException() : base("Cross-tenant access refused.") { }

    public CrossTenantAccessException(string message) : base(message) { }

    public CrossTenantAccessException(string message, Exception innerException) : base(message, innerException) { }
}
