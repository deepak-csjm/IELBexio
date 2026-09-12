using IelBexio.Domain.Common;
using IelBexio.Domain.Invoicing;
using IelBexio.Domain.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IelBexio.Infrastructure.Persistence.Configurations;

/// <summary>Shared column conventions so money and identifiers are typed consistently everywhere.</summary>
internal static class ColumnTypes
{
    /// <summary>All monetary amounts. numeric is exact — never float or double (§21).</summary>
    public const string Money = "numeric(19,4)";

    /// <summary>Tax rates as percentages, e.g. 8.100000.</summary>
    public const string Rate = "numeric(9,6)";

    /// <summary>Quantities, which may legitimately be fractional (weight, hours).</summary>
    public const string Quantity = "numeric(18,6)";

    /// <summary>Workflow signals in [0,1].</summary>
    public const string Signal = "numeric(5,4)";

    public const string Sha256 = "char(64)";
    public const string Json = "jsonb";
}

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable("invoices");
        b.HasKey(x => x.Id);

        b.Property(x => x.SourceDocumentId).HasMaxLength(200).IsRequired();
        b.Property(x => x.SourceDocumentVersion).HasMaxLength(100).IsRequired();
        b.Property(x => x.InvoiceNumber).HasMaxLength(100);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.BexioInvoiceId).HasMaxLength(100);
        b.Property(x => x.BexioContactId).HasMaxLength(100);
        b.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
        b.Property(x => x.PreparedBy).HasMaxLength(256);
        b.Property(x => x.ApprovedBy).HasMaxLength(256);
        b.Property(x => x.VerifiedBy).HasMaxLength(256);

        foreach (var money in new[]
                 {
                     nameof(Invoice.SubtotalAmount), nameof(Invoice.DiscountAmount), nameof(Invoice.ShippingAmount),
                     nameof(Invoice.TaxAmount), nameof(Invoice.TotalAmount),
                 })
        {
            b.Property<decimal>(money).HasColumnType(ColumnTypes.Money);
        }

        // WorkflowState has a private setter by design: only InvoiceWorkflow may change it. EF needs to
        // be told to read and write the backing field directly.
        b.Property(x => x.WorkflowState)
            .HasConversion<int>()
            .UsePropertyAccessMode(PropertyAccessMode.Property)
            .IsRequired();

        b.OwnsOne(x => x.BillingAddress, a =>
        {
            a.Property(p => p.Line1).HasColumnName("billing_line1").HasMaxLength(300);
            a.Property(p => p.Line2).HasColumnName("billing_line2").HasMaxLength(300);
            a.Property(p => p.PostalCode).HasColumnName("billing_postal_code").HasMaxLength(30);
            a.Property(p => p.City).HasColumnName("billing_city").HasMaxLength(150);
            a.Property(p => p.Region).HasColumnName("billing_region").HasMaxLength(150);
            a.Property(p => p.CountryCode).HasColumnName("billing_country_code").HasMaxLength(2);
        });

        b.OwnsOne(x => x.ShippingAddress, a =>
        {
            a.Property(p => p.Line1).HasColumnName("shipping_line1").HasMaxLength(300);
            a.Property(p => p.Line2).HasColumnName("shipping_line2").HasMaxLength(300);
            a.Property(p => p.PostalCode).HasColumnName("shipping_postal_code").HasMaxLength(30);
            a.Property(p => p.City).HasColumnName("shipping_city").HasMaxLength(150);
            a.Property(p => p.Region).HasColumnName("shipping_region").HasMaxLength(150);
            a.Property(p => p.CountryCode).HasColumnName("shipping_country_code").HasMaxLength(2);
        });

        b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(l => l.Invoice!).HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Payments).WithOne().HasForeignKey(p => p.InvoiceId).OnDelete(DeleteBehavior.Cascade);

        // ---- The constraint that makes repeated imports safe (§19, acceptance criterion 14) -------
        b.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceDocumentId, x.SourceDocumentVersion })
            .IsUnique()
            .HasDatabaseName("ux_invoices_source_identity");

        // One canonical invoice per Bexio invoice: a second posting cannot claim the same Bexio id.
        b.HasIndex(x => new { x.TenantId, x.BexioInvoiceId })
            .IsUnique()
            .HasFilter("bexio_invoice_id IS NOT NULL")
            .HasDatabaseName("ux_invoices_bexio_invoice_id");

        b.HasIndex(x => new { x.TenantId, x.WorkflowState }).HasDatabaseName("ix_invoices_tenant_state");
        b.HasIndex(x => new { x.TenantId, x.InvoiceNumber }).HasDatabaseName("ix_invoices_tenant_number");
        b.HasIndex(x => new { x.TenantId, x.CreatedAt }).HasDatabaseName("ix_invoices_tenant_created");
        b.HasIndex(x => x.CorrelationId).HasDatabaseName("ix_invoices_correlation");
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.ToTable("invoice_lines");
        b.HasKey(x => x.Id);

        b.Property(x => x.Description).HasMaxLength(2000).IsRequired();
        b.Property(x => x.Sku).HasMaxLength(200);
        b.Property(x => x.ProductCode).HasMaxLength(200);
        b.Property(x => x.SourceLineId).HasMaxLength(200);
        b.Property(x => x.Unit).HasMaxLength(50);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.BexioArticleId).HasMaxLength(100);
        b.Property(x => x.BexioAccountId).HasMaxLength(100);
        b.Property(x => x.BexioTaxId).HasMaxLength(100);

        b.Property(x => x.Quantity).HasColumnType(ColumnTypes.Quantity);
        b.Property(x => x.UnitPrice).HasColumnType(ColumnTypes.Money);
        b.Property(x => x.DiscountAmount).HasColumnType(ColumnTypes.Money);
        b.Property(x => x.NetAmount).HasColumnType(ColumnTypes.Money);
        b.Property(x => x.TaxAmount).HasColumnType(ColumnTypes.Money);
        b.Property(x => x.GrossAmount).HasColumnType(ColumnTypes.Money);
        b.Property(x => x.TaxRatePercent).HasColumnType(ColumnTypes.Rate);

        b.HasIndex(x => new { x.TenantId, x.InvoiceId }).HasDatabaseName("ix_invoice_lines_invoice");
        b.HasIndex(x => new { x.TenantId, x.Sku }).HasDatabaseName("ix_invoice_lines_sku");
    }
}

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("customers");
        b.HasKey(x => x.Id);

        b.Property(x => x.CompanyName).HasMaxLength(300);
        b.Property(x => x.FirstName).HasMaxLength(150);
        b.Property(x => x.LastName).HasMaxLength(150);
        b.Property(x => x.Email).HasMaxLength(320);
        b.Property(x => x.Phone).HasMaxLength(60);
        b.Property(x => x.VatNumber).HasMaxLength(60);
        b.Property(x => x.CountryCode).HasMaxLength(2);
        b.Property(x => x.SourceCustomerId).HasMaxLength(200);
        b.Property(x => x.ExternalIdentifiersJson).HasColumnType(ColumnTypes.Json);

        b.OwnsOne(x => x.Address, a =>
        {
            a.Property(p => p.Line1).HasColumnName("line1").HasMaxLength(300);
            a.Property(p => p.Line2).HasColumnName("line2").HasMaxLength(300);
            a.Property(p => p.PostalCode).HasColumnName("postal_code").HasMaxLength(30);
            a.Property(p => p.City).HasColumnName("city").HasMaxLength(150);
            a.Property(p => p.Region).HasColumnName("region").HasMaxLength(150);
            a.Property(p => p.CountryCode).HasColumnName("address_country_code").HasMaxLength(2);
        });

        b.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceCustomerId })
            .IsUnique()
            .HasFilter("source_customer_id IS NOT NULL")
            .HasDatabaseName("ux_customers_source_identity");

        b.HasIndex(x => new { x.TenantId, x.Email }).HasDatabaseName("ix_customers_email");
        b.HasIndex(x => new { x.TenantId, x.CompanyName }).HasDatabaseName("ix_customers_company");
    }
}

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Amount).HasColumnType(ColumnTypes.Money);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Method).HasMaxLength(100);
        b.Property(x => x.Status).HasMaxLength(60);
        b.Property(x => x.SourcePaymentId).HasMaxLength(200);
        b.Property(x => x.TransactionReference).HasMaxLength(200);
        b.HasIndex(x => new { x.TenantId, x.InvoiceId }).HasDatabaseName("ix_payments_invoice");
    }
}

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("tenants");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(300).IsRequired();
        b.Property(x => x.CountryCode).HasMaxLength(2);
        b.Property(x => x.DefaultCurrency).HasMaxLength(3).IsRequired();
    }
}

internal sealed class ApprovalConfiguration : IEntityTypeConfiguration<Approval>
{
    public void Configure(EntityTypeBuilder<Approval> b)
    {
        b.ToTable("approvals");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        b.Property(x => x.ApprovedBy).HasMaxLength(256).IsRequired();
        b.Property(x => x.PreparedBy).HasMaxLength(256);
        b.Property(x => x.ApprovalVersion).HasMaxLength(64).IsRequired();
        b.Property(x => x.Comment).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.HasIndex(x => new { x.TenantId, x.EntityId, x.ApprovedAt }).HasDatabaseName("ix_approvals_entity");
    }
}
