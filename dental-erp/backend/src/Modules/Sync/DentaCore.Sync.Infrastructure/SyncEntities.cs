namespace DentaCore.Sync.Infrastructure;

/// <summary>
/// Oxuna bilən (pull/snapshot) obyektin təsviri. Cədvəl alias-ı həmişə <c>t</c>-dir. JSON yalnız açıq siyahıdakı sahələri daşıyır:
/// şifrələnmiş və yüksək həssas sahələr (telefon, e-poçt, FIN, ünvan, qeydlər) cihaza heç vaxt getmir. Bunlar yalnız onlayn
/// <c>reveal</c> ilə, audit olunaraq oxunur; itirilmiş cihazdan sızma səthi beləcə azalır.
/// </summary>
/// <param name="Name">cədvəl adı (change_log.entity ilə eyni).</param>
/// <param name="Permissions">istənilən biri kifayətdir; ən yüksək scope seçilir.</param>
/// <param name="Json">jsonb_build_object ifadəsi (camelCase açarlar).</param>
/// <param name="Join">scope üçün əlavə JOIN (alias p).</param>
/// <param name="BranchExpr">filial scope-u üçün SQL ifadəsi; null = filialsız (istinad məlumatı).</param>
/// <param name="OwnExpr">own scope-u üçün predikat (@me parametri).</param>
/// <param name="Alive">silinməmiş şərti (soft-delete).</param>
/// <param name="Visible">scope-dan asılı olmayan əlavə görünürlük şərti.</param>
internal sealed record SyncEntity(string Name, string[] Permissions, string Json, string Join, string? BranchExpr, string OwnExpr, string Alive, string Visible);

internal static class SyncEntities
{
    /// <summary>"Baxan həkim" əlaqəsi (ICareRelationships ilə eyni qayda): həkimin pasiyentlə ləğv olunmamış qəbulu var.</summary>
    private static string Care(string patientIdExpr) =>
        "EXISTS (SELECT 1 FROM appointments a WHERE a.patient_id = " + patientIdExpr + " AND a.provider_id = @me AND a.status NOT IN ('cancelled', 'rescheduled'))";

    public static readonly IReadOnlyList<SyncEntity> All =
    [
        new("branches", ["appointment:read", "patient:read", "invoice:read", "clinical:read"],
            "jsonb_build_object('code', t.code, 'name', t.name, 'isActive', t.is_active)",
            string.Empty, "t.id", "true", "true", "true"),
        new("rooms", ["appointment:read"],
            "jsonb_build_object('branchId', t.branch_id, 'name', t.name, 'kind', t.kind, 'isActive', t.is_active)",
            string.Empty, "t.branch_id", "true", "true", "true"),
        new("patients", ["patient:read"],
            "jsonb_build_object('chartNo', t.chart_no, 'branchId', t.branch_id, 'firstName', t.first_name, 'lastName', t.last_name, 'fatherName', t.father_name, " +
            "'birthDate', t.birth_date, 'gender', t.gender, 'preferredChannel', t.preferred_channel, 'status', t.status, 'noShowCount', t.no_show_count)",
            string.Empty, "t.branch_id", "(t.created_by = @me OR " + Care("t.id") + ")", "t.deleted_at IS NULL", "true"),
        new("appointments", ["appointment:read"],
            "jsonb_build_object('branchId', t.branch_id, 'patientId', t.patient_id, 'providerId', t.provider_id, 'roomId', t.room_id, " +
            "'start', lower(t.period), 'end', upper(t.period), 'status', t.status, 'reason', t.reason)",
            string.Empty, "t.branch_id", "t.provider_id = @me", "true", "true"),
        new("clinical_notes", ["clinical:read"],
            "jsonb_build_object('patientId', t.patient_id, 'visitId', t.visit_id, 'authorId', t.author_id, 'subjective', t.subjective, 'objective', t.objective, " +
            "'assessment', t.assessment, 'plan', t.plan, 'source', t.source, 'signedAt', t.signed_at, 'addendumOf', t.addendum_of, 'createdAt', t.created_at)",
            "JOIN patients p ON p.id = t.patient_id", "p.branch_id",
            "(t.author_id = @me OR " + Care("t.patient_id") + ")",
            "true", "(t.signed_at IS NOT NULL OR t.author_id = @me)"),   // qaralamalar yalnız müəllifə (adi API ilə eyni qayda)
        new("services", ["invoice:read"],
            "jsonb_build_object('code', t.code, 'name', t.name, 'category', t.category, 'price', t.price, 'currency', t.currency, 'vatRate', t.vat_rate, 'isActive', t.is_active)",
            string.Empty, null, "true", "true", "true"),
    ];
}
