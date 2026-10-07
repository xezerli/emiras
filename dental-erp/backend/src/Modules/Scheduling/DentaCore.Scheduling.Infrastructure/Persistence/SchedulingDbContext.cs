using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.Scheduling.Application;
using DentaCore.Scheduling.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NpgsqlTypes;

namespace DentaCore.Scheduling.Infrastructure.Persistence;

/// <summary>Cədvəllər T004 miqrasiyasındadır. Double-booking qorunması DB-dəki EXCLUDE constraint-lərdədir.</summary>
public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options), ISchedulingUnitOfWork
{
    public DbSet<Appointment> Appointments => Set<Appointment>();

    public DbSet<QueueTicket> QueueTickets => Set<QueueTicket>();

    public DbSet<AppointmentReminder> Reminders => Set<AppointmentReminder>();

    public DbSet<WaitlistEntry> Waitlist => Set<WaitlistEntry>();

    public System.Reflection.Assembly ApplicationAssembly => typeof(ISchedulingUnitOfWork).Assembly;

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

        // tstzrange [Start, End) ↔ TimeSlot. DİQQƏT: NpgsqlRange-in 2 arqumentli konstruktoru hər iki sərhədi DAXİL edir ([a,b]),
        // bu da ardıcıl qəbulları (10:00–10:30, 10:30–11:00) üst-üstə salardı. Üst sərhəd açıq göstərilməlidir.
        var slotConverter = new ValueConverter<TimeSlot, NpgsqlRange<DateTime>>(
            v => new NpgsqlRange<DateTime>(v.Start.UtcDateTime, true, v.End.UtcDateTime, false),
            v => new TimeSlot(
                new DateTimeOffset(DateTime.SpecifyKind(v.LowerBound, DateTimeKind.Utc)),
                new DateTimeOffset(DateTime.SpecifyKind(v.UpperBound, DateTimeKind.Utc))));
        var statusConverter = new ValueConverter<AppointmentStatus, string>(
            v => ToDb(v),
            v => FromDb(v));
        var queueStatus = new ValueConverter<QueueStatus, string>(v => v.ToString().ToLowerInvariant(), v => Enum.Parse<QueueStatus>(v, true));

        modelBuilder.Entity<Appointment>(b =>
        {
            b.ToTable("appointments");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.HoldsSlot);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.ProviderId).HasColumnName("provider_id");
            b.Property(x => x.RoomId).HasColumnName("room_id");
            b.Property(x => x.Slot).HasColumnName("period").HasColumnType("tstzrange").HasConversion(slotConverter);
            b.Property(x => x.Status).HasColumnName("status").HasConversion(statusConverter);
            b.Property(x => x.Reason).HasColumnName("reason");
            b.Property(x => x.Source).HasColumnName("source");
            b.Property(x => x.CancelReason).HasColumnName("cancel_reason");
            b.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
            b.Property(x => x.CheckedInAt).HasColumnName("checked_in_at");
            b.Property(x => x.CreatedBy).HasColumnName("created_by");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
        });

        modelBuilder.Entity<QueueTicket>(b =>
        {
            b.ToTable("queue_tickets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.AppointmentId).HasColumnName("appointment_id");
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.TicketNo).HasColumnName("ticket_no");
            b.Property(x => x.QueueDate).HasColumnName("queue_date");
            b.Property(x => x.Status).HasColumnName("status").HasConversion(queueStatus);
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
            b.Property(x => x.CalledAt).HasColumnName("called_at");
            // FK-nı modeldə bəyan edirik ki, EF INSERT sırasını bilsin (appointment əvvəl, bilet sonra)
            b.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<AppointmentReminder>(b =>
        {
            b.ToTable("appointment_reminders");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.AppointmentId).HasColumnName("appointment_id");
            b.Property(x => x.Channel).HasColumnName("channel");
            b.Property(x => x.SendAt).HasColumnName("send_at");
            b.Property(x => x.Status).HasColumnName("status");
            b.HasOne<Appointment>().WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<WaitlistEntry>(b =>
        {
            b.ToTable("waitlist_entries");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.PatientId).HasColumnName("patient_id");
            b.Property(x => x.BranchId).HasColumnName("branch_id");
            b.Property(x => x.ProviderId).HasColumnName("provider_id");
            b.Property(x => x.Earliest).HasColumnName("earliest");
            b.Property(x => x.Latest).HasColumnName("latest");
            b.Property(x => x.Priority).HasColumnName("priority");
        });
    }

    private static string ToDb(AppointmentStatus s) => s switch
    {
        AppointmentStatus.CheckedIn => "checked_in",
        AppointmentStatus.InProgress => "in_progress",
        AppointmentStatus.NoShow => "no_show",
        _ => s.ToString().ToLowerInvariant(),
    };

    private static AppointmentStatus FromDb(string s) => s switch
    {
        "checked_in" => AppointmentStatus.CheckedIn,
        "in_progress" => AppointmentStatus.InProgress,
        "no_show" => AppointmentStatus.NoShow,
        _ => Enum.Parse<AppointmentStatus>(s, true),
    };
}
