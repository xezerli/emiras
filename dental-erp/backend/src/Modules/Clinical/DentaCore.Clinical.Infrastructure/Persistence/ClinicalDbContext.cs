using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.Clinical.Application;
using DentaCore.Clinical.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DentaCore.Clinical.Infrastructure.Persistence;

/// <summary>Cədvəllər T005 və T008 miqrasiyalarındadır.</summary>
public sealed class ClinicalDbContext(DbContextOptions<ClinicalDbContext> options) : DbContext(options), IClinicalUnitOfWork
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<Visit> Visits => Set<Visit>();

    public DbSet<ToothRecord> ToothRecords => Set<ToothRecord>();

    public DbSet<TreatmentPlan> Plans => Set<TreatmentPlan>();

    public DbSet<Prescription> Prescriptions => Set<Prescription>();

    public DbSet<ClinicalNote> Notes => Set<ClinicalNote>();

    public System.Reflection.Assembly ApplicationAssembly => typeof(IClinicalUnitOfWork).Assembly;

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

        var visitStatus = new ValueConverter<VisitStatus, string>(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<VisitStatus>(v, true));
        var condition = new ValueConverter<ToothCondition, string>(
            v => v == ToothCondition.RootCanal ? "root_canal" : v == ToothCondition.PeriapicalLesion ? "periapical_lesion" : v.ToString().ToLowerInvariant(),
            v => Enum.Parse<ToothCondition>(v.Replace("_", string.Empty, StringComparison.Ordinal), true));
        var planStatus = new ValueConverter<PlanStatus, string>(
            v => v == PlanStatus.InProgress ? "in_progress" : v.ToString().ToLowerInvariant(),
            v => Enum.Parse<PlanStatus>(v.Replace("_", string.Empty, StringComparison.Ordinal), true));
        var itemStatus = new ValueConverter<ItemStatus, string>(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<ItemStatus>(v, true));
        var prescriptionItems = new ValueConverter<IReadOnlyList<PrescriptionItem>, string>(
            v => JsonSerializer.Serialize(v, Json),
            v => JsonSerializer.Deserialize<List<PrescriptionItem>>(v, Json)!);
        var prescriptionItemsComparer = new ValueComparer<IReadOnlyList<PrescriptionItem>>(
            (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
            v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
            v => v.ToList());

        modelBuilder.Entity<Visit>(b =>
        {
            b.ToTable("visits");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.AppointmentId).HasColumnName("appointment_id");
            b.Property(x => x.ProviderId).HasColumnName("provider_id");
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.StartedAt).HasColumnName("started_at");
            b.Property(x => x.EndedAt).HasColumnName("ended_at");
            b.Property(x => x.ChiefComplaint).HasColumnName("chief_complaint");
            b.Property(x => x.Status).HasColumnName("status").HasConversion(visitStatus);
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
        });

        modelBuilder.Entity<ToothRecord>(b =>
        {
            b.ToTable("tooth_records");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.VisitId).HasColumnName("visit_id");
            b.Property(x => x.ToothFdi).HasColumnName("tooth_fdi").HasColumnType("smallint");
            b.Property(x => x.Surface).HasColumnName("surface").HasConversion<string>();
            b.Property(x => x.Condition).HasColumnName("condition").HasConversion(condition);
            b.Property(x => x.Material).HasColumnName("material");
            b.Property(x => x.Notes).HasColumnName("notes");
            b.Property(x => x.RecordedBy).HasColumnName("recorded_by");
            b.Property(x => x.RecordedAt).HasColumnName("recorded_at");
            b.Property(x => x.SupersededAt).HasColumnName("superseded_at");
        });

        modelBuilder.Entity<TreatmentPlan>(b =>
        {
            b.ToTable("treatment_plans");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.Total);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.ProviderId).HasColumnName("provider_id");
            b.Property(x => x.Title).HasColumnName("title");
            b.Property(x => x.Status).HasColumnName("status").HasConversion(planStatus);
            b.Property(x => x.Version).HasColumnName("version");
            b.Property(x => x.AcceptedAt).HasColumnName("accepted_at");
            b.Property(x => x.AiGenerated).HasColumnName("ai_generated");
            b.Property(x => x.AiModel).HasColumnName("ai_model");
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
            b.Property<DateTimeOffset>("created_at").HasColumnName("created_at").ValueGeneratedOnAdd();   // siyahını sıralamaq üçün (DB default now())
            b.HasMany(x => x.Items).WithOne().HasForeignKey(i => i.PlanId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PlanItem>(b =>
        {
            b.ToTable("treatment_plan_items");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.LineTotal);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PlanId).HasColumnName("plan_id");
            b.Property(x => x.ProcedureCode).HasColumnName("procedure_code");
            b.Property(x => x.ToothFdi).HasColumnName("tooth_fdi").HasColumnType("smallint");
            b.Property(x => x.Surface).HasColumnName("surface").HasConversion<string>();
            b.Property(x => x.Phase).HasColumnName("phase").HasColumnType("smallint");
            b.Property(x => x.Quantity).HasColumnName("quantity");
            b.Property(x => x.UnitPrice).HasColumnName("unit_price");
            b.Property(x => x.DiscountPercent).HasColumnName("discount_percent");
            b.Property(x => x.Status).HasColumnName("status").HasConversion(itemStatus);
            b.Property(x => x.PerformedVisitId).HasColumnName("performed_visit_id");
            b.Property(x => x.PerformedAt).HasColumnName("performed_at");
            b.Property(x => x.PerformedBy).HasColumnName("performed_by");
            b.Property(x => x.SortOrder).HasColumnName("sort_order");
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
        });

        modelBuilder.Entity<Prescription>(b =>
        {
            b.ToTable("prescriptions");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.RowVersion);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.VisitId).HasColumnName("visit_id");
            b.Property(x => x.ProviderId).HasColumnName("provider_id");
            b.Property(x => x.IssuedAt).HasColumnName("issued_at");
            b.Property(x => x.Items).HasColumnName("items").HasColumnType("jsonb").HasConversion(prescriptionItems, prescriptionItemsComparer);
            b.Property(x => x.AllergyCheckPassed).HasColumnName("allergy_check_passed");
            b.Property(x => x.OverrideReason).HasColumnName("override_reason");
        });

        modelBuilder.Entity<ClinicalNote>(b =>
        {
            b.ToTable("clinical_notes");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.IsSigned);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.VisitId).HasColumnName("visit_id");
            b.Property(x => x.AuthorId).HasColumnName("author_id");
            b.Property(x => x.Subjective).HasColumnName("subjective");
            b.Property(x => x.Objective).HasColumnName("objective");
            b.Property(x => x.Assessment).HasColumnName("assessment");
            b.Property(x => x.Plan).HasColumnName("plan");
            b.Property(x => x.Source).HasColumnName("source");
            b.Property(x => x.SignedAt).HasColumnName("signed_at");
            b.Property(x => x.AddendumOf).HasColumnName("addendum_of");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
        });
    }
}
