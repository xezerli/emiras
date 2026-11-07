using DentaCore.Billing.Domain;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Scheduling.Contracts;

namespace DentaCore.Billing.Application;

public static class BillingPermissions
{
    public const string InvoiceRead = "invoice:read";
    public const string InvoiceWrite = "invoice:write";
    public const string PaymentWrite = "payment:write";
    public const string Refund = "invoice:refund";
    public const string Settings = "settings:manage";
}

public sealed class BillingOptions
{
    public const string Section = "Billing";

    public string DefaultCurrency { get; set; } = "AZN";

    /// <summary>"Bu gün" və "vaxtı keçmiş" klinikanın yerli tarixinə görə hesablanır (UTC ilə gecə yarısı səhvi olmasın).</summary>
    public string TimeZone { get; set; } = "Asia/Baku";
}

internal static class BillingClock
{
    public static DateOnly Today(IClock clock, Microsoft.Extensions.Options.IOptions<BillingOptions> options)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
    }
}

public interface IBillingUnitOfWork : IModuleUnitOfWork
{
    /// <summary>Uğursuz saxlamadan sonra izlənən (Added) obyektləri atır ki, sonrakı SaveChanges onları təkrar yazmağa çalışmasın.</summary>
    void DiscardChanges();
}

public sealed record InvoiceFilter(Guid? PatientId, InvoiceStatus? Status, bool Overdue, DateOnly Today, IReadOnlyCollection<Guid>? BranchIds, Guid? OwnUserId, IReadOnlyCollection<Guid>? OwnPatientIds);

public sealed record InvoicePage(IReadOnlyList<Invoice> Items, string? NextCursor);

/// <summary>Rolun geri qaytarma limiti (role_permissions.max_amount). Max null = limitsiz.</summary>
public sealed record RefundLimit(bool HasPermission, decimal? Max);

public interface IRefundLimits
{
    Task<RefundLimit> GetAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IBillingRepository
{
    // Qiymət siyahısı
    void Add(Service service);

    Task<Service?> GetServiceAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Service>> GetServicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    Task<IReadOnlyList<Service>> ListServicesAsync(string? query, bool includeInactive, CancellationToken cancellationToken);

    // Faktura
    void Add(Invoice invoice);

    /// <summary>İzlənən (tracked) faktura: dəyişdirilib saxlanacaqsa.</summary>
    Task<Invoice?> GetInvoiceAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>İzlənməyən nüsxə: yalnız oxuma/icazə yoxlaması üçün. Sonradan FOR UPDATE ilə yüklənəndə köhnəlmiş izlənən nüsxə qarşıya çıxmasın.</summary>
    Task<Invoice?> GetInvoiceSnapshotAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Sətri <c>FOR UPDATE</c> ilə kilidləyib yükləyir. Çağıran tranzaksiya açmış olmalıdır: paralel ödənişlər ardıcıllaşır.</summary>
    Task<Invoice?> GetInvoiceForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<InvoicePage> ListInvoicesAsync(InvoiceFilter filter, string? cursor, int limit, CancellationToken cancellationToken);

    // Ödəniş
    void Add(Payment payment);

    Task<Payment?> GetPaymentAsync(Guid id, CancellationToken cancellationToken);

    Task<Payment?> FindPaymentByKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<Payment>> ListPaymentsAsync(Guid invoiceId, CancellationToken cancellationToken);

    Task<decimal> SumRefundedAsync(Guid paymentId, CancellationToken cancellationToken);

    // Kassa
    void Add(CashShift shift);

    Task<CashShift?> GetOpenShiftAsync(Guid cashierId, CancellationToken cancellationToken);

    Task<CashShift?> GetShiftAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Ödəniş smeni <c>FOR SHARE</c>, bağlama <c>FOR UPDATE</c> ilə kilidləyir: smen bağlanarkən ona ödəniş düşə bilməz.
    /// Kilid alındıqdan sonrakı AÇIQ vəziyyəti DB-dən qaytarır (izlənən nüsxə köhnə ola bilər).
    /// </summary>
    Task<bool> LockShiftAsync(Guid id, bool exclusive, CancellationToken cancellationToken);

    /// <summary>Smendəki nağd hərəkət: qəbul olunan − geri qaytarılan.</summary>
    Task<decimal> NetCashAsync(Guid shiftId, CancellationToken cancellationToken);
}

/// <summary>
/// Fakturaya giriş qərarı: icazə + filial/own scope. Scope xaricində "tapılmadı" qaytarılır ki, başqa filialın faktura nömrələri sızmasın.
/// own scope-lu həkim üçün: provayderi və ya yaradanı özüdür, ya da pasiyentə baxan həkimdir.
/// </summary>
public sealed class BillingAccess(ICurrentUser user, ICareRelationships care)
{
    public async Task<bool> CanSeeAsync(Invoice invoice, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (user.CanAccess(permission, invoice.BranchId, invoice.CreatedBy))
        {
            return true;
        }

        return user.ScopeOf(permission) == PermissionScope.Own
            && (invoice.ProviderId == user.UserId || await care.HasAsync(user.UserId, invoice.PatientId, cancellationToken));
    }

    public static Error NotFound() => Error.NotFound("invoice.not_found", "Invoice not found.");
}

internal static class BillingText
{
    public static string Status(InvoiceStatus s) => s switch
    {
        InvoiceStatus.PartiallyPaid => "partially_paid",
        _ => s.ToString().ToLowerInvariant(),
    };

    public static bool TryParseStatus(string value, out InvoiceStatus status) =>
        Enum.TryParse(value.Replace("_", string.Empty, StringComparison.Ordinal), true, out status) && Enum.IsDefined(status);

    public static System.Text.Json.JsonSerializerOptions Json { get; } = new(System.Text.Json.JsonSerializerDefaults.Web);

    public static string ToJson(object value) => System.Text.Json.JsonSerializer.Serialize(value, Json);
}
