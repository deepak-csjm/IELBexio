namespace IelBexio.Domain.Common;

/// <summary>Base for every persisted entity. Identifiers are GUIDv7 so they sort by creation time.</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Base for every <em>business</em> entity. Carrying <see cref="TenantId"/> on the base type means a
/// developer cannot forget it, and the EF global query filter can be applied uniformly (§23).
/// </summary>
public abstract class TenantEntity : Entity
{
    public Guid TenantId { get; set; }
}
