using System.Data.Common;
using System.Text;
using DentaCore.Patient.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace DentaCore.Patient.Infrastructure.Persistence;

internal sealed class PatientReadModel(PatientDbContext db) : IPatientReadModel
{
    public async Task<IReadOnlyList<PatientListRow>> SearchAsync(PatientSearchCriteria c, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(c);
        var sql = new StringBuilder("""
            SELECT p.id, p.chart_no, p.branch_id, p.first_name, p.last_name, p.birth_date, p.phone_enc, p.created_at
            FROM patients p
            WHERE p.deleted_at IS NULL
            """);
        var args = new List<DbParameter>();

        if (c.AllowedBranchIds is not null)
        {
            if (c.AllowedBranchIds.Count == 0 && c.OwnerUserId is null)
            {
                return [];   // heç bir filiala icazəsi yoxdur
            }

            if (c.AllowedBranchIds.Count > 0)
            {
                sql.Append(" AND p.branch_id = ANY(@branches)");
                args.Add(new NpgsqlParameter("branches", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = c.AllowedBranchIds.ToArray() });
            }
        }

        if (c.BranchId is { } branch)
        {
            sql.Append(" AND p.branch_id = @branch");
            args.Add(new NpgsqlParameter("branch", NpgsqlDbType.Uuid) { Value = branch });
        }

        if (c.OwnerUserId is { } owner)
        {
            sql.Append(" AND p.created_by = @owner");
            args.Add(new NpgsqlParameter("owner", NpgsqlDbType.Uuid) { Value = owner });
        }

        if (c.HasTextFilter)
        {
            var alternatives = new List<string>();
            if (c.NameTokens.Length > 0)
            {
                // Hər token ayrıca LIKE (sıra fərqi yoxdur: "Əli Əliyev" və "Əliyev Əli"). lower(last||' '||first) GIN trigram index-inə uyğundur.
                var parts = new List<string>();
                for (var i = 0; i < c.NameTokens.Length; i++)
                {
                    parts.Add($"lower(p.last_name || ' ' || p.first_name) LIKE lower(@t{i}) ESCAPE '\\'");
                    args.Add(new NpgsqlParameter($"t{i}", NpgsqlDbType.Text) { Value = "%" + EscapeLike(c.NameTokens[i]) + "%" });
                }

                alternatives.Add("(" + string.Join(" AND ", parts) + ")");
            }

            if (c.PhoneHash is not null)
            {
                alternatives.Add("p.phone_hash = @phoneHash");
                args.Add(new NpgsqlParameter("phoneHash", NpgsqlDbType.Bytea) { Value = c.PhoneHash });
            }

            if (c.NationalIdHash is not null)
            {
                alternatives.Add("p.national_id_hash = @nidHash");
                args.Add(new NpgsqlParameter("nidHash", NpgsqlDbType.Bytea) { Value = c.NationalIdHash });
            }

            if (c.ChartNo is { } chart)
            {
                alternatives.Add("p.chart_no = @chart");
                args.Add(new NpgsqlParameter("chart", NpgsqlDbType.Bigint) { Value = chart });
            }

            sql.Append(" AND (").Append(string.Join(" OR ", alternatives)).Append(')');
        }

        if (c.After is { } after)
        {
            sql.Append(" AND (p.created_at, p.id) < (@afterTs, @afterId)");
            args.Add(new NpgsqlParameter("afterTs", NpgsqlDbType.TimestampTz) { Value = after.CreatedAt.UtcDateTime });
            args.Add(new NpgsqlParameter("afterId", NpgsqlDbType.Uuid) { Value = after.Id });
        }

        sql.Append(" ORDER BY p.created_at DESC, p.id DESC LIMIT @limit");
        args.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = c.Limit });

        return await db.Database.SqlQueryRaw<PatientListRow>(sql.ToString(), args.ToArray()).ToListAsync(cancellationToken);
    }

    public async Task<MedicalProfileData> GetMedicalProfileAsync(Guid patientId, CancellationToken cancellationToken)
    {
        var allergies = await db.Database
            .SqlQuery<AllergyRow>($"SELECT id, substance, reaction, severity FROM patient_allergies WHERE patient_id = {patientId} AND is_active ORDER BY created_at")
            .ToListAsync(cancellationToken);
        var conditions = await db.Database
            .SqlQuery<ConditionRow>($"SELECT icd10_code AS icd10code, name, since FROM patient_conditions WHERE patient_id = {patientId} AND is_active ORDER BY created_at")
            .ToListAsync(cancellationToken);
        var medications = await db.Database
            .SqlQuery<MedicationRow>($"SELECT name, dose, frequency FROM patient_medications WHERE patient_id = {patientId} AND (ended_on IS NULL OR ended_on >= current_date) ORDER BY created_at")
            .ToListAsync(cancellationToken);
        var anamnesis = await db.Database
            .SqlQuery<string>($"SELECT history::text AS \"Value\" FROM anamnesis WHERE patient_id = {patientId} ORDER BY recorded_at DESC LIMIT 1")
            .ToListAsync(cancellationToken);

        return new MedicalProfileData(allergies, conditions, medications, anamnesis.FirstOrDefault());
    }

    /// <summary>LIKE joker simvolları (% _ \) istifadəçi daxilində literal sayılır: "%" yazmaq bütün pasiyentləri qaytarmasın.</summary>
    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
}
