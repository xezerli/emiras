using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Clinical.Domain;

public sealed record PrescriptionItem(string Drug, string Dose, string? Frequency, int? Days, string? Notes);

public sealed record PrescriptionIssued(Guid EventId, DateTimeOffset OccurredAt, Guid PrescriptionId, Guid PatientId, Guid ProviderId, bool AllergyOverride)
    : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "clinical.prescription-issued";
}

/// <summary>Resept dəyişməzdir (düzəliş = yeni resept). Allergiya xəbərdarlığı keçilibsə səbəb məcburidir.</summary>
public sealed class Prescription : AggregateRoot<Guid>
{
    private Prescription()
        : base(Guid.Empty)
    {
    }

    public Guid PatientId { get; private set; }

    public Guid? VisitId { get; private set; }

    public Guid ProviderId { get; private set; }

    public DateTimeOffset IssuedAt { get; private set; }

    public IReadOnlyList<PrescriptionItem> Items { get; private set; } = [];

    public bool AllergyCheckPassed { get; private set; }

    public string? OverrideReason { get; private set; }

    public static Result<Prescription> Issue(
        Guid id, Guid patientId, Guid? visitId, Guid providerId, IReadOnlyList<PrescriptionItem> items, bool hadConflict, string? overrideReason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is 0 or > 20)
        {
            return Error.Validation("prescription.invalid_items", "A prescription needs between 1 and 20 items.");
        }

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Drug) || item.Drug.Length > 200 || string.IsNullOrWhiteSpace(item.Dose) || item.Dose.Length > 100
                || item.Frequency is { Length: > 100 } || item.Notes is { Length: > 500 } || item.Days is < 1 or > 365)
            {
                return Error.Validation("prescription.invalid_item", "Each item needs a drug (max 200) and dose (max 100); days 1-365.");
            }
        }

        if (hadConflict && string.IsNullOrWhiteSpace(overrideReason))
        {
            return Error.Validation("prescription.override_reason_required", "Prescribing despite an allergy warning requires a reason.");
        }

        if (hadConflict && overrideReason!.Trim().Length < 10)
        {
            return Error.Validation("prescription.override_reason_required", "The override reason must be at least 10 characters.");
        }

        var prescription = new Prescription
        {
            Id = id,
            PatientId = patientId,
            VisitId = visitId,
            ProviderId = providerId,
            IssuedAt = now,
            Items = items.Select(i => i with { Drug = i.Drug.Trim(), Dose = i.Dose.Trim() }).ToList(),
            AllergyCheckPassed = !hadConflict,
            OverrideReason = hadConflict ? overrideReason!.Trim() : null,
        };
        prescription.Raise(new PrescriptionIssued(Guid.NewGuid(), now, id, patientId, providerId, hadConflict));
        return prescription;
    }
}

public sealed record AllergyConflict(string Drug, string Allergen, string Severity);

/// <summary>
/// Sadə, şəffaf allergiya yoxlaması: ad uyğunluğu + kiçik dərman sinfi lüğəti (penisillin ailəsi və s.).
/// Bu klinik qərar dəstəyi vasitəsidir, həkimin mühakiməsini əvəz etmir və tam dərman bazası DEYİL
/// (Faza 3-də dərman məlumat bazası və AI ilə genişlənəcək).
/// </summary>
public static class AllergyChecker
{
    private static readonly string[][] Classes =
    [
        ["penisillin", "penicillin", "amoksisillin", "amoxicillin", "ampisillin", "ampicillin", "amoksiklav", "augmentin", "flemoksin", "bensilpenisillin"],
        ["sefalosporin", "cephalosporin", "sefaleksin", "cephalexin", "seftriakson", "ceftriaxone", "sefuroksim", "cefuroxime"],
        ["nsaid", "ibuprofen", "diklofenak", "diclofenac", "naproksen", "naproxen", "ketoprofen", "ketorolak", "ketorolac", "aspirin", "asetilsalisil"],
        ["lidokain", "lidocaine", "articaine", "artikain", "ultracain", "mepivakain", "mepivacaine", "novokain", "novocaine", "prokain", "procaine", "anestezik"],
        ["lateks", "latex"],
        ["makrolid", "macrolide", "eritromisin", "erythromycin", "azitromisin", "azithromycin", "klaritromisin", "clarithromycin"],
        ["metronidazol", "metronidazole", "trixomonasid", "flagyl"],
        ["klindamisin", "clindamycin", "dalacin"],
    ];

    public static IReadOnlyList<AllergyConflict> Check(IEnumerable<string> drugs, IEnumerable<(string Substance, string Severity)> allergies)
    {
        ArgumentNullException.ThrowIfNull(drugs);
        ArgumentNullException.ThrowIfNull(allergies);
        var allergyList = allergies.ToList();
        var conflicts = new List<AllergyConflict>();
        foreach (var drug in drugs)
        {
            var normalizedDrug = Normalize(drug);
            foreach (var (substance, severity) in allergyList)
            {
                if (Matches(normalizedDrug, Normalize(substance)))
                {
                    conflicts.Add(new AllergyConflict(drug.Trim(), substance.Trim(), severity));
                }
            }
        }

        return conflicts;
    }

    private static bool Matches(string drug, string allergen)
    {
        if (allergen.Length < 3)
        {
            return false;   // 1-2 hərfli qeydlərlə təsadüfi uyğunluq olmasın
        }

        if (drug.Contains(allergen, StringComparison.Ordinal) || allergen.Contains(drug, StringComparison.Ordinal))
        {
            return true;
        }

        // Eyni dərman sinfi: allergen sinfin istənilən üzvüdürsə, sinfin hər üzvü uyğun gəlir
        return Classes.Any(c => c.Any(member => allergen.Contains(member, StringComparison.Ordinal)) && c.Any(member => drug.Contains(member, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Kiçik hərf + diakritiklərin silinməsi. Böyük "İ" (nöqtəli, Azərbaycan/türk) ToLowerInvariant ilə "i̇" (i + birləşən nöqtə) verir,
    /// FormD ilə ayrılıb nöqtə atılır; ə, ı kimi parçalanmayan hərflər əl ilə xəritələnir.
    /// </summary>
    private static string Normalize(string value)
    {
        var decomposed = value.Trim().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().ToLowerInvariant().Replace('ə', 'e').Replace('ı', 'i');
    }
}
