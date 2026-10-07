using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Patient.Domain;

/// <summary>Şifrələnmiş dəyər + dəqiq axtarış üçün blind-index heş. Domen açıq mətni görmür.</summary>
public sealed record ProtectedValue(byte[] Cipher, byte[] Hash);

public sealed record PatientRegistered(Guid EventId, DateTimeOffset OccurredAt, Guid PatientId, Guid BranchId) : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "patient.registered";
}

/// <summary>Yalnız dəyişən sahələrin ADLARI (PHI dəyərləri hadisəyə düşmür).</summary>
public sealed record PatientUpdated(Guid EventId, DateTimeOffset OccurredAt, Guid PatientId, IReadOnlyList<string> ChangedFields) : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "patient.updated";
}

public sealed record PatientDeleted(Guid EventId, DateTimeOffset OccurredAt, Guid PatientId) : DomainEvent(EventId, OccurredAt)
{
    public override string RoutingKey => "patient.deleted";
}

/// <summary>PATCH üçün: hər sahə Optional (verilməyib / verilib).</summary>
public sealed record PatientPatch
{
    public Optional<string> FirstName { get; init; }

    public Optional<string> LastName { get; init; }

    public Optional<string?> FatherName { get; init; }

    public Optional<DateOnly?> BirthDate { get; init; }

    public Optional<char?> Gender { get; init; }

    public Optional<ProtectedValue?> NationalId { get; init; }

    public Optional<ProtectedValue?> Phone { get; init; }

    public Optional<ProtectedValue?> Email { get; init; }

    public Optional<string?> AddressJson { get; init; }

    public Optional<string> PreferredChannel { get; init; }

    public Optional<bool> MarketingOptIn { get; init; }

    public Optional<string?> ReferralSource { get; init; }

    public Optional<string?> Notes { get; init; }
}

public sealed class Patient : AggregateRoot<Guid>
{
    private static readonly string[] Channels = ["sms", "whatsapp", "telegram", "email", "push", "none"];
    private static readonly DateOnly OldestPlausibleBirth = new(1900, 1, 1);

    private Patient()
        : base(Guid.Empty)
    {
    }

    /// <summary>Oxunaqlı kart nömrəsi: DB identity yaradır, qeydiyyatdan sonra məlum olur.</summary>
    public long ChartNo { get; private set; }

    public Guid BranchId { get; private set; }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    public string? FatherName { get; private set; }

    public DateOnly? BirthDate { get; private set; }

    public char? Gender { get; private set; }

    public ProtectedValue? NationalId { get; private set; }

    public ProtectedValue? Phone { get; private set; }

    public ProtectedValue? Email { get; private set; }

    public string? AddressJson { get; private set; }

    public string PreferredChannel { get; private set; } = "sms";

    public bool MarketingOptIn { get; private set; }

    public string? ReferralSource { get; private set; }

    public string? Notes { get; private set; }

    public int NoShowCount { get; private set; }

    public decimal? RiskScore { get; private set; }

    public string Status { get; private set; } = "active";

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public static Result<Patient> Register(
        Guid id,
        Guid branchId,
        string firstName,
        string lastName,
        string? fatherName,
        DateOnly? birthDate,
        char? gender,
        ProtectedValue? nationalId,
        ProtectedValue? phone,
        ProtectedValue? email,
        string? addressJson,
        string preferredChannel,
        bool marketingOptIn,
        string? referralSource,
        Guid createdBy,
        DateTimeOffset now)
    {
        var patient = new Patient
        {
            Id = id,
            BranchId = branchId,
            NationalId = nationalId,
            Phone = phone,
            Email = email,
            AddressJson = addressJson,
            MarketingOptIn = marketingOptIn,
            ReferralSource = referralSource,
            CreatedBy = createdBy,
            CreatedAt = now,
        };

        var validation = patient.ApplyCore(
            new PatientPatch
            {
                FirstName = Optional.Some(firstName),
                LastName = Optional.Some(lastName),
                FatherName = Optional.Some<string?>(fatherName),
                BirthDate = Optional.Some(birthDate),
                Gender = Optional.Some(gender),
                PreferredChannel = Optional.Some(preferredChannel),
            },
            DateOnly.FromDateTime(now.UtcDateTime));
        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        patient.Raise(new PatientRegistered(Guid.NewGuid(), now, id, branchId));
        return patient;
    }

    /// <summary>Dəyişiklikləri tətbiq edir. Hər şey ya tam tətbiq olunur, ya heç nə (validasiya xətasında vəziyyət dəyişmir).</summary>
    public Result<IReadOnlyList<string>> Apply(PatientPatch patch, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (DeletedAt is not null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        var validation = Validate(patch, DateOnly.FromDateTime(now.UtcDateTime));
        if (validation.IsFailure)
        {
            return validation.Error!;
        }

        var changed = ApplyUnchecked(patch);
        if (changed.Count > 0)
        {
            Raise(new PatientUpdated(Guid.NewGuid(), now, Id, changed));
        }

        return Result.Success<IReadOnlyList<string>>(changed);
    }

    public Result SoftDelete(DateTimeOffset now)
    {
        if (DeletedAt is not null)
        {
            return Error.NotFound("patient.not_found", "Patient not found.");
        }

        DeletedAt = now;
        Raise(new PatientDeleted(Guid.NewGuid(), now, Id));
        return Result.Success();
    }

    private Result ApplyCore(PatientPatch patch, DateOnly today)
    {
        var validation = Validate(patch, today);
        if (validation.IsSuccess)
        {
            ApplyUnchecked(patch);
        }

        return validation;
    }

    private static Result Validate(PatientPatch p, DateOnly today)
    {
        if (p.FirstName.HasValue && !ValidName(p.FirstName.Value))
        {
            return Error.Validation("patient.invalid_first_name", "First name is required (max 100 characters).");
        }

        if (p.LastName.HasValue && !ValidName(p.LastName.Value))
        {
            return Error.Validation("patient.invalid_last_name", "Last name is required (max 100 characters).");
        }

        if (p.BirthDate is { HasValue: true, Value: { } birth } && (birth > today || birth < OldestPlausibleBirth))
        {
            return Error.Validation("patient.invalid_birth_date", "Birth date is not plausible.");
        }

        if (p.Gender is { HasValue: true, Value: { } g } && g is not ('M' or 'F' or 'O'))
        {
            return Error.Validation("patient.invalid_gender", "Gender must be M, F or O.");
        }

        if (p.PreferredChannel.HasValue && !Channels.Contains(p.PreferredChannel.Value))
        {
            return Error.Validation("patient.invalid_channel", "Unsupported contact channel.");
        }

        return Result.Success();
    }

    private static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 100;

    private List<string> ApplyUnchecked(PatientPatch p)
    {
        var changed = new List<string>();

        void Set<T>(Optional<T> opt, string field, Func<T?> get, Action<T?> set)
        {
            if (opt.HasValue && !EqualityComparer<T?>.Default.Equals(get(), opt.Value))
            {
                set(opt.Value);
                changed.Add(field);
            }
        }

        // Şifrələnmiş sahələr müqayisəsi: heş (blind index) dəyişibsə dəyişib sayılır
        void SetProtected(Optional<ProtectedValue?> opt, string field, Func<ProtectedValue?> get, Action<ProtectedValue?> set)
        {
            if (!opt.HasValue)
            {
                return;
            }

            var current = get();
            var same = (current is null && opt.Value is null)
                || (current is not null && opt.Value is not null && current.Hash.AsSpan().SequenceEqual(opt.Value.Hash));
            if (!same)
            {
                set(opt.Value);
                changed.Add(field);
            }
        }

        Set(p.FirstName, "firstName", () => FirstName, v => FirstName = v!.Trim());
        Set(p.LastName, "lastName", () => LastName, v => LastName = v!.Trim());
        Set(p.FatherName, "fatherName", () => FatherName, v => FatherName = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        Set(p.BirthDate, "birthDate", () => BirthDate, v => BirthDate = v);
        Set(p.Gender, "gender", () => Gender, v => Gender = v);
        SetProtected(p.NationalId, "nationalId", () => NationalId, v => NationalId = v);
        SetProtected(p.Phone, "phone", () => Phone, v => Phone = v);
        SetProtected(p.Email, "email", () => Email, v => Email = v);
        Set(p.AddressJson, "address", () => AddressJson, v => AddressJson = v);
        Set(p.PreferredChannel, "preferredChannel", () => PreferredChannel, v => PreferredChannel = v!);
        Set(p.MarketingOptIn, "marketingOptIn", () => MarketingOptIn, v => MarketingOptIn = v);
        Set(p.ReferralSource, "referralSource", () => ReferralSource, v => ReferralSource = v);
        Set(p.Notes, "notes", () => Notes, v => Notes = v);
        return changed;
    }
}

public enum AllergySeverity
{
    Mild,
    Moderate,
    Severe,
    Anaphylaxis,
}

public sealed class PatientAllergy : Entity<Guid>
{
    private PatientAllergy()
        : base(Guid.Empty)
    {
    }

    public Guid PatientId { get; private set; }

    public string Substance { get; private set; } = string.Empty;

    public string? Reaction { get; private set; }

    public AllergySeverity Severity { get; private set; }

    public bool IsActive { get; private set; } = true;

    public static Result<PatientAllergy> Create(Guid id, Guid patientId, string substance, string? reaction, AllergySeverity severity)
    {
        if (string.IsNullOrWhiteSpace(substance) || substance.Trim().Length > 200)
        {
            return Error.Validation("allergy.invalid_substance", "Substance is required (max 200 characters).");
        }

        return new PatientAllergy
        {
            Id = id,
            PatientId = patientId,
            Substance = substance.Trim(),
            Reaction = string.IsNullOrWhiteSpace(reaction) ? null : reaction.Trim(),
            Severity = severity,
        };
    }
}
