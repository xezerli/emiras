using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using DentaCore.Dashboard.Application;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace DentaCore.Dashboard.Infrastructure;

/// <summary>
/// Oxuma modeli: Scheduling, Billing və Patient cədvəllərini yalnız oxuyur (hesabat konteksti; modulların yazı qaydalarına toxunmur).
/// Bütün sorğular bir bağlantıdadır. Filial filtri hər yerdə eyni parametrdir (NULL = hamısı).
/// Günün sərhədləri klinikanın qurşağında hesablanır: [gün 00:00, növbəti gün 00:00).
/// </summary>
internal sealed class SqlDashboardReader(ITenantDataSources dataSources, ITenantContext tenant, IOptions<DashboardOptions> options) : IDashboardReader
{
    private const string Window = """
        WITH w AS (
            SELECT ($1::date::timestamp AT TIME ZONE $2) AS day_start,
                   (($1::date + 1)::timestamp AT TIME ZONE $2) AS day_end,
                   (date_trunc('month', $1::date)::timestamp AT TIME ZONE $2) AS month_start)
        """;

    public async Task<DashboardSummaryDto> ReadAsync(IReadOnlyCollection<Guid>? branches, DateOnly date, CancellationToken cancellationToken)
    {
        await using var connection = await dataSources.Get(tenant.Schema).OpenConnectionAsync(cancellationToken);
        var zone = options.Value.TimeZone;
        var branchArray = branches?.ToArray();

        NpgsqlCommand Command(string sql)
        {
            var command = new NpgsqlCommand(sql, connection);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = date });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = zone });
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid, Value = (object?)branchArray ?? DBNull.Value });
            return command;
        }

        // $3 = filial siyahısı (NULL = hamısı)
        AppointmentsToday appointments;
        await using (var c = Command(Window + """
            SELECT count(*) FILTER (WHERE a.status NOT IN ('cancelled', 'rescheduled'))::int,
                   count(*) FILTER (WHERE a.status = 'completed')::int,
                   count(*) FILTER (WHERE a.status = 'no_show')::int,
                   count(*) FILTER (WHERE a.status = 'checked_in')::int
            FROM appointments a, w
            WHERE lower(a.period) >= w.day_start AND lower(a.period) < w.day_end AND ($3::uuid[] IS NULL OR a.branch_id = ANY($3))
            """))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
        {
            await r.ReadAsync(cancellationToken);
            appointments = new AppointmentsToday(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
        }

        decimal today;
        decimal month;
        await using (var c = Command(Window + """
            SELECT COALESCE(sum(CASE p.kind WHEN 'refund' THEN -p.amount ELSE p.amount END) FILTER (WHERE p.paid_at >= w.day_start AND p.paid_at < w.day_end), 0),
                   COALESCE(sum(CASE p.kind WHEN 'refund' THEN -p.amount ELSE p.amount END) FILTER (WHERE p.paid_at >= w.month_start AND p.paid_at < w.day_end), 0)
            FROM payments p JOIN invoices i ON i.id = p.invoice_id, w
            WHERE p.paid_at >= w.month_start AND p.paid_at < w.day_end AND ($3::uuid[] IS NULL OR i.branch_id = ANY($3))
            """))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
        {
            await r.ReadAsync(cancellationToken);
            today = r.GetDecimal(0);
            month = r.GetDecimal(1);
        }

        decimal debt;
        await using (var c = Command("""
            SELECT COALESCE(sum(total - insurance_amount - paid_total), 0) FROM invoices
            WHERE kind = 'invoice' AND status IN ('issued', 'partially_paid') AND ($3::uuid[] IS NULL OR branch_id = ANY($3))
            """))
        {
            debt = (decimal)(await c.ExecuteScalarAsync(cancellationToken))!;
        }

        int newPatients;
        await using (var c = Command(Window + """
            SELECT count(*)::int FROM patients p, w
            WHERE p.deleted_at IS NULL AND p.created_at >= w.day_start AND p.created_at < w.day_end AND ($3::uuid[] IS NULL OR p.branch_id = ANY($3))
            """))
        {
            newPatients = (int)(await c.ExecuteScalarAsync(cancellationToken))!;
        }

        var services = new List<TopService>();
        await using (var c = Command(Window + """
            SELECT ii.description, sum(ii.quantity)::int, sum(ii.line_total)
            FROM invoice_items ii JOIN invoices i ON i.id = ii.invoice_id, w
            WHERE i.status IN ('issued', 'partially_paid', 'paid') AND i.issued_at >= w.month_start AND i.issued_at < w.day_end
              AND ($3::uuid[] IS NULL OR i.branch_id = ANY($3))
            GROUP BY ii.description ORDER BY sum(ii.line_total) DESC, ii.description LIMIT 5
            """))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                services.Add(new TopService(r.GetString(0), r.GetInt32(1), r.GetDecimal(2)));
            }
        }

        var providers = new List<TopProvider>();
        await using (var c = Command(Window + """
            SELECT ii.provider_id, u.full_name, sum(ii.line_total)
            FROM invoice_items ii JOIN invoices i ON i.id = ii.invoice_id JOIN users u ON u.id = ii.provider_id, w
            WHERE i.status IN ('issued', 'partially_paid', 'paid') AND i.issued_at >= w.month_start AND i.issued_at < w.day_end
              AND ($3::uuid[] IS NULL OR i.branch_id = ANY($3))
            GROUP BY ii.provider_id, u.full_name ORDER BY sum(ii.line_total) DESC, u.full_name LIMIT 5
            """))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                providers.Add(new TopProvider(r.GetGuid(0), r.GetString(1), r.GetDecimal(2)));
            }
        }

        var rooms = new List<RoomState>();
        await using (var c = Command("""
            SELECT r.id, r.name, EXISTS (SELECT 1 FROM appointments a WHERE a.room_id = r.id AND a.status = 'in_progress')
            FROM rooms r WHERE r.is_active AND ($3::uuid[] IS NULL OR r.branch_id = ANY($3)) ORDER BY r.name, r.id
            """))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                rooms.Add(new RoomState(r.GetGuid(0), r.GetString(1), r.GetBoolean(2) ? "occupied" : "free"));
            }
        }

        var series = new List<RevenuePoint>();
        await using (var c = Command("""
            SELECT d::date, COALESCE(s.amount, 0)
            FROM generate_series($1::date - 29, $1::date, interval '1 day') d
            LEFT JOIN (
                SELECT (p.paid_at AT TIME ZONE $2)::date AS day, sum(CASE p.kind WHEN 'refund' THEN -p.amount ELSE p.amount END) AS amount
                FROM payments p JOIN invoices i ON i.id = p.invoice_id
                WHERE p.paid_at >= (($1::date - 29)::timestamp AT TIME ZONE $2) AND p.paid_at < (($1::date + 1)::timestamp AT TIME ZONE $2)
                  AND ($3::uuid[] IS NULL OR i.branch_id = ANY($3))
                GROUP BY 1) s ON s.day = d::date
            ORDER BY 1
            """))
        await using (var r = await c.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                series.Add(new RevenuePoint(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetDecimal(1)));
            }
        }

        return new DashboardSummaryDto(date, appointments, today, month, debt, newPatients, services, providers, rooms, series);
    }
}
