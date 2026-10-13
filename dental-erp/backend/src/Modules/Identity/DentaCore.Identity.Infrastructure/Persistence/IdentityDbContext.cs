using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Outbox;
using DentaCore.Identity.Application;
using DentaCore.Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DentaCore.Identity.Infrastructure.Persistence;

/// <summary>
/// Cədvəllər T002 migration-ında yaradılıb. EF yalnız mapping üçündür, sxemi EF yaratmır (Mərhələ 2 §7).
/// Hər sorğuda tenant-ın sxemi connection-un search_path-ı ilə seçilir.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options), IIdentityUnitOfWork
{
    public DbSet<User> Users => Set<User>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public System.Reflection.Assembly ApplicationAssembly => typeof(IIdentityUnitOfWork).Assembly;

    async Task<IUnitOfWorkTransaction> IUnitOfWork.BeginTransactionAsync(CancellationToken cancellationToken) =>
        new EfTransaction(await Database.BeginTransactionAsync(cancellationToken));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ConfigureOutbox();

        var statusConverter = new ValueConverter<UserStatus, string>(
            v => v.ToString().ToLowerInvariant(),
            v => Enum.Parse<UserStatus>(v, true));

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.EmailHash).HasColumnName("email_hash");
            b.Property(x => x.EmailEnc).HasColumnName("email_enc");
            b.Property(x => x.PasswordHash).HasColumnName("password_hash");
            b.Property(x => x.FullName).HasColumnName("full_name");
            b.Property(x => x.Status).HasColumnName("status").HasConversion(statusConverter);
            b.Property(x => x.FailedLogins).HasColumnName("failed_logins");
            b.Property(x => x.LockedUntil).HasColumnName("locked_until");
            b.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            b.Property(x => x.TwoFactorEnabled).HasColumnName("two_factor_enabled");
            // row_version-ı DB trigger-i artırır (T001 trg_touch): optimistic concurrency token
            b.Property(x => x.RowVersion).HasColumnName("row_version").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
        });

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.ToTable("refresh_tokens");
            b.HasKey(x => x.Id);
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.RowVersion);   // bu cədvəldə row_version yoxdur
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.UserId).HasColumnName("user_id");
            b.Property(x => x.DeviceId).HasColumnName("device_id");
            b.Property(x => x.FamilyId).HasColumnName("family_id");
            b.Property(x => x.TokenHash).HasColumnName("token_hash");
            b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            b.Property(x => x.UsedAt).HasColumnName("used_at");
            b.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            b.Property(x => x.ReplacedBy).HasColumnName("replaced_by");
            b.Property(x => x.Ip).HasColumnName("ip").HasColumnType("inet");
            b.Property(x => x.CreatedAt).HasColumnName("created_at");
        });
    }
}
