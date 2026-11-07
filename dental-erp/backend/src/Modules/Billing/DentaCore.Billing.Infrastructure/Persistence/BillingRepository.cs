using System.Globalization;
using DentaCore.Billing.Application;
using DentaCore.Billing.Domain;
using Microsoft.EntityFrameworkCore;

namespace DentaCore.Billing.Infrastructure.Persistence;

internal sealed class BillingRepository(BillingDbContext db) : IBillingRepository
{
    public void Add(Service service) => db.Services.Add(service);

    public void Add(Invoice invoice) => db.Invoices.Add(invoice);

    public void Add(Payment payment) => db.Payments.Add(payment);

    public void Add(CashShift shift) => db.Shifts.Add(shift);

    public Task<Service?> GetServiceAsync(Guid id, CancellationToken cancellationToken) => db.Services.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Service>> GetServicesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.Services.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Service>> ListServicesAsync(string? query, bool includeInactive, CancellationToken cancellationToken)
    {
        var services = db.Services.AsNoTracking().Where(s => includeInactive || s.IsActive);
        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = "%" + EscapeLike(query.Trim()) + "%";
            services = services.Where(s => EF.Functions.ILike(s.Name, pattern, "\\") || EF.Functions.ILike(s.Code, pattern, "\\"));
        }

        return await services.OrderBy(s => s.Category).ThenBy(s => s.Name).Take(500).ToListAsync(cancellationToken);
    }

    public Task<Invoice?> GetInvoiceAsync(Guid id, CancellationToken cancellationToken) =>
        db.Invoices.Include(i => i.Items).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public Task<Invoice?> GetInvoiceSnapshotAsync(Guid id, CancellationToken cancellationToken) =>
        db.Invoices.AsNoTracking().Include(i => i.Items).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    public async Task<Invoice?> GetInvoiceForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM invoices WHERE id = {id} FOR UPDATE", cancellationToken);
        return await db.Invoices.Include(i => i.Items).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task<InvoicePage> ListInvoicesAsync(InvoiceFilter filter, string? cursor, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = db.Invoices.AsNoTracking().Include(i => i.Items).AsQueryable();
        if (filter.OwnUserId is { } me)
        {
            var patients = filter.OwnPatientIds ?? [];
            query = query.Where(i => i.ProviderId == me || i.CreatedBy == me || patients.Contains(i.PatientId));
        }
        else if (filter.BranchIds is { } branches)
        {
            query = query.Where(i => branches.Contains(i.BranchId));
        }

        if (filter.PatientId is { } patientId)
        {
            query = query.Where(i => i.PatientId == patientId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(i => i.Status == status);
        }

        if (filter.Overdue)
        {
            var today = filter.Today;
            query = query.Where(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid) && i.DueDate < today);
        }

        if (TryParseCursor(cursor, out var at, out var id))
        {
            query = query.Where(i => i.CreatedAt < at || (i.CreatedAt == at && i.Id.CompareTo(id) < 0));
        }

        var rows = await query.OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id).Take(limit + 1).ToListAsync(cancellationToken);
        if (rows.Count <= limit)
        {
            return new InvoicePage(rows, null);
        }

        rows.RemoveAt(rows.Count - 1);
        var last = rows[^1];
        return new InvoicePage(rows, string.Create(CultureInfo.InvariantCulture, $"{last.CreatedAt.UtcTicks}_{last.Id}"));
    }

    public Task<Payment?> GetPaymentAsync(Guid id, CancellationToken cancellationToken) => db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<Payment?> FindPaymentByKeyAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<IReadOnlyList<Payment>> ListPaymentsAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        await db.Payments.AsNoTracking().Where(p => p.InvoiceId == invoiceId).OrderBy(p => p.PaidAt).ThenBy(p => p.Id).ToListAsync(cancellationToken);

    public async Task<decimal> SumRefundedAsync(Guid paymentId, CancellationToken cancellationToken) =>
        await db.Payments.AsNoTracking().Where(p => p.RefundOf == paymentId).SumAsync(p => p.Amount, cancellationToken);

    public Task<CashShift?> GetOpenShiftAsync(Guid cashierId, CancellationToken cancellationToken) =>
        db.Shifts.FirstOrDefaultAsync(s => s.CashierId == cashierId && s.ClosedAt == null, cancellationToken);

    public Task<CashShift?> GetShiftAsync(Guid id, CancellationToken cancellationToken) => db.Shifts.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<bool> LockShiftAsync(Guid id, bool exclusive, CancellationToken cancellationToken)
    {
        const string share = "SELECT (closed_at IS NULL) AS \"Value\" FROM cash_shifts WHERE id = {0} FOR SHARE";
        const string update = "SELECT (closed_at IS NULL) AS \"Value\" FROM cash_shifts WHERE id = {0} FOR UPDATE";
        var rows = await db.Database.SqlQueryRaw<bool>(exclusive ? update : share, id).ToListAsync(cancellationToken);
        return rows.Count == 1 && rows[0];
    }

    public async Task<decimal> NetCashAsync(Guid shiftId, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<decimal>(
            $"SELECT COALESCE(SUM(CASE WHEN kind = 'refund' THEN -amount ELSE amount END), 0) AS \"Value\" FROM payments WHERE shift_id = {shiftId} AND method = 'cash'")
            .SingleAsync(cancellationToken);

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static bool TryParseCursor(string? cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        var parts = cursor?.Split('_');
        if (parts is not { Length: 2 } || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParse(parts[1], out id))
        {
            return false;
        }

        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        at = new DateTimeOffset(ticks, TimeSpan.Zero);
        return true;
    }
}

internal sealed class RefundLimits(BillingDbContext db) : IRefundLimits
{
    public async Task<RefundLimit> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<decimal?>(
            $"SELECT rp.max_amount AS \"Value\" FROM user_roles ur JOIN role_permissions rp ON rp.role_id = ur.role_id WHERE ur.user_id = {userId} AND rp.permission_code = 'invoice:refund'")
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new RefundLimit(false, 0m);
        }

        // Rollardan biri limitsizdirsə (NULL) limitsiz; əks halda ən yüksək limit
        return rows.Any(r => r is null) ? new RefundLimit(true, null) : new RefundLimit(true, rows.Max());
    }
}
