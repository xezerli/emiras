using DentaCore.Billing.Application;
using DentaCore.Billing.Domain;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DentaCore.Billing.Infrastructure.Persistence;

/// <summary>Cədvəllər T006 və T010 miqrasiyalarındadır.</summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options), IBillingUnitOfWork
{
    public DbSet<Service> Services => Set<Service>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<CashShift> Shifts => Set<CashShift>();

    public System.Reflection.Assembly ApplicationAssembly => typeof(IBillingUnitOfWork).Assembly;

    public void DiscardChanges() => ChangeTracker.Clear();

    async Task<IUnitOfWorkTransaction> IUnitOfWork.BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfTransaction(await Database.BeginTransactionAsync(cancellationToken));

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException ex) when (PostgresErrors.Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ConfigureOutbox();

        var kind = new ValueConverter<InvoiceKind, string>(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<InvoiceKind>(v, true));
        var status = new ValueConverter<InvoiceStatus, string>(
            v => v == InvoiceStatus.PartiallyPaid ? "partially_paid" : v.ToString().ToLowerInvariant(),
            v => Enum.Parse<InvoiceStatus>(v.Replace("_", string.Empty, StringComparison.Ordinal), true));
        var paymentKind = new ValueConverter<PaymentKind, string>(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<PaymentKind>(v, true));

        modelBuilder.Entity<Service>(b =>
        {
            b.ToTable("services");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.Code).HasColumnName("code");
            b.Property(x => x.ProcedureCode).HasColumnName("procedure_code");
            b.Property(x => x.Name).HasColumnName("name");
            b.Property(x => x.Category).HasColumnName("category");
            b.Property(x => x.Price).HasColumnName("price");
            b.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            b.Property(x => x.VatRate).HasColumnName("vat_rate");
            b.Property(x => x.IsActive).HasColumnName("is_active");
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
        });

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("invoices");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.Balance);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.Number).HasColumnName("number").ValueGeneratedOnAdd();   // DB default: INV-YYYY-000001
            b.Property(x => x.Kind).HasColumnName("kind").HasConversion(kind);
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.ProviderId).HasColumnName("provider_id");
            b.Property(x => x.VisitId).HasColumnName("visit_id");
            b.Property(x => x.PlanId).HasColumnName("plan_id");
            b.Property(x => x.Status).HasColumnName("status").HasConversion(status);
            b.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            b.Property(x => x.Subtotal).HasColumnName("subtotal");
            b.Property(x => x.DiscountTotal).HasColumnName("discount_total");
            b.Property(x => x.TaxTotal).HasColumnName("tax_total");
            b.Property(x => x.Total).HasColumnName("total");
            b.Property(x => x.PaidTotal).HasColumnName("paid_total");
            b.Property(x => x.InsuranceAmount).HasColumnName("insurance_amount");
            b.Property(x => x.IssuedAt).HasColumnName("issued_at");
            b.Property(x => x.DueDate).HasColumnName("due_date");
            b.Property(x => x.Notes).HasColumnName("notes");
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
            b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.InvoiceId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<InvoiceItem>(b =>
        {
            b.ToTable("invoice_items");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.LineTotal);   // DB-də generated column
            b.Ignore(x => x.LineTax);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            b.Property(x => x.ServiceId).HasColumnName("service_id");
            b.Property(x => x.PlanItemId).HasColumnName("plan_item_id");
            b.Property(x => x.Description).HasColumnName("description");
            b.Property(x => x.ToothFdi).HasColumnName("tooth_fdi").HasColumnType("smallint");
            b.Property(x => x.ProviderId).HasColumnName("provider_id");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.UnitPrice).HasColumnName("unit_price");
            b.Property(x => x.Discount).HasColumnName("discount");
            b.Property(x => x.VatRate).HasColumnName("vat_rate");
        });

        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.InvoiceId).HasColumnName("invoice_id");
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.ShiftId).HasColumnName("shift_id");
            b.Property(x => x.Kind).HasColumnName("kind").HasConversion(paymentKind);
            b.Property(x => x.Method).HasColumnName("method");
            b.Property(x => x.Amount).HasColumnName("amount");
            b.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)");
            b.Property(x => x.Reference).HasColumnName("reference");
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            b.Property(x => x.ReceivedBy).HasColumnName("received_by");
            b.Property(x => x.PaidAt).HasColumnName("paid_at");
            b.Property(x => x.RefundOf).HasColumnName("refund_of");
            b.Property(x => x.Reason).HasColumnName("reason");
        });

        modelBuilder.Entity<CashShift>(b =>
        {
            b.ToTable("cash_shifts");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.IsOpen);
            b.Ignore(x => x.Difference);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.CashierId).HasColumnName("cashier_id");
            b.Property(x => x.OpenedAt).HasColumnName("opened_at");
            b.Property(x => x.ClosedAt).HasColumnName("closed_at");
            b.Property(x => x.OpeningCash).HasColumnName("opening_cash");
            b.Property(x => x.ClosingCash).HasColumnName("closing_cash");
            b.Property(x => x.ExpectedCash).HasColumnName("expected_cash");
        });
    }
}
