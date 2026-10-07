using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.Patient.Application;
using DentaCore.Patient.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DentaCore.Patient.Infrastructure.Persistence;

/// <summary>Cədvəllər T003 miqrasiyasındadır. EF yalnız mapping üçündür, sxemi yaratmır.</summary>
public sealed class PatientDbContext(DbContextOptions<PatientDbContext> options) : DbContext(options), IPatientUnitOfWork
{
    public DbSet<Domain.Patient> Patients => Set<Domain.Patient>();

    public DbSet<PatientAllergy> Allergies => Set<PatientAllergy>();

    public System.Reflection.Assembly ApplicationAssembly => typeof(IPatientUnitOfWork).Assembly;

    async Task<IUnitOfWorkTransaction> IUnitOfWork.BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfTransaction(await Database.BeginTransactionAsync(cancellationToken));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ConfigureOutbox();

        modelBuilder.Entity<Domain.Patient>(b =>
        {
            b.ToTable("patients");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.HasQueryFilter(x => x.DeletedAt == null);   // soft-delete: silinmişlər heç bir sorğuda görünmür
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.ChartNo).HasColumnName("chart_no").ValueGeneratedOnAdd();   // GENERATED ALWAYS AS IDENTITY
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.FirstName).HasColumnName("first_name");
            b.Property(x => x.LastName).HasColumnName("last_name");
            b.Property(x => x.FatherName).HasColumnName("father_name");
            b.Property(x => x.BirthDate).HasColumnName("birth_date");
            b.Property(x => x.Gender).HasColumnName("gender");
            b.Property(x => x.AddressJson).HasColumnName("address").HasColumnType("jsonb");
            b.Property(x => x.PreferredChannel).HasColumnName("preferred_channel");
            b.Property(x => x.MarketingOptIn).HasColumnName("marketing_opt_in");
            b.Property(x => x.ReferralSource).HasColumnName("referral_source");
            b.Property(x => x.Notes).HasColumnName("notes");
            b.Property(x => x.NoShowCount).HasColumnName("no_show_count");
            b.Property(x => x.RiskScore).HasColumnName("risk_score").ValueGeneratedOnAdd();
            b.Property(x => x.Status).HasColumnName("status").ValueGeneratedOnAdd();
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
            Protected(b, x => x.NationalId, "national_id");
            Protected(b, x => x.Phone, "phone");
            Protected(b, x => x.Email, "email");
        });

        var severity = new ValueConverter<AllergySeverity, string>(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<AllergySeverity>(v, true));
        modelBuilder.Entity<PatientAllergy>(b =>
        {
            b.ToTable("patient_allergies");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.Substance).HasColumnName("substance");
            b.Property(x => x.Reaction).HasColumnName("reaction");
            b.Property(x => x.Severity).HasColumnName("severity").HasConversion(severity);
            b.Property(x => x.IsActive).HasColumnName("is_active");
        });
    }

    /// <summary>PHI sahəsi iki sütundur: *_enc (AES-GCM) və *_hash (blind index).</summary>
    private static void Protected(
        EntityTypeBuilder<Domain.Patient> b,
        System.Linq.Expressions.Expression<Func<Domain.Patient, ProtectedValue?>> property,
        string column) =>
        b.OwnsOne(property, o =>
        {
            o.Property(v => v.Cipher).HasColumnName($"{column}_enc");
            o.Property(v => v.Hash).HasColumnName($"{column}_hash");
        });
}
