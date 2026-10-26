using DentaCore.BuildingBlocks.Domain;
using DentaCore.Patient.Domain;

namespace DentaCore.Patient.UnitTests;

public class PatientDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid Creator = Guid.NewGuid();

    private static ProtectedValue Pv(string text) => new(System.Text.Encoding.UTF8.GetBytes("enc:" + text), System.Text.Encoding.UTF8.GetBytes("hash:" + text));

    private static Result<Domain.Patient> Register(
        string first = "Aysel",
        string last = "Qasımova",
        DateOnly? birth = null,
        char? gender = 'F',
        string channel = "sms",
        ProtectedValue? phone = null) =>
        Domain.Patient.Register(Guid.NewGuid(), Branch, first, last, null, birth, gender, null, phone, null, null, channel, false, null, Creator, Now);

    [Fact]
    public void Register_succeeds_and_raises_event()
    {
        var result = Register(birth: new DateOnly(1990, 5, 1), phone: Pv("+994501234567"));

        Assert.True(result.IsSuccess);
        Assert.Equal("Aysel", result.Value.FirstName);
        Assert.Equal(Creator, result.Value.CreatedBy);
        Assert.IsType<PatientRegistered>(Assert.Single(result.Value.DomainEvents));
    }

    [Theory]
    [InlineData("", "Qasımova", "patient.invalid_first_name")]
    [InlineData("  ", "Qasımova", "patient.invalid_first_name")]
    [InlineData("Aysel", "", "patient.invalid_last_name")]
    public void Names_are_required(string first, string last, string code) =>
        Assert.Equal(code, Register(first, last).Error!.Code);

    [Fact]
    public void Name_longer_than_100_is_rejected() =>
        Assert.True(Register(first: new string('a', 101)).IsFailure);

    [Fact]
    public void Future_or_implausible_birth_date_is_rejected()
    {
        Assert.Equal("patient.invalid_birth_date", Register(birth: new DateOnly(2026, 10, 8)).Error!.Code);
        Assert.Equal("patient.invalid_birth_date", Register(birth: new DateOnly(1899, 12, 31)).Error!.Code);
        Assert.True(Register(birth: new DateOnly(2026, 10, 7)).IsSuccess);   // bu gün doğulan körpə
    }

    [Fact]
    public void Gender_and_channel_are_validated()
    {
        Assert.Equal("patient.invalid_gender", Register(gender: 'X').Error!.Code);
        Assert.Equal("patient.invalid_channel", Register(channel: "pigeon").Error!.Code);
    }

    [Fact]
    public void Apply_returns_only_really_changed_fields_and_raises_one_event()
    {
        var patient = Register(phone: Pv("+994501234567")).Value;
        patient.ClearDomainEvents();

        var result = patient.Apply(
            new PatientPatch
            {
                FirstName = Optional.Some("Aysel"),                        // eyni
                LastName = Optional.Some("Əliyeva"),                       // dəyişir
                Phone = Optional.Some<ProtectedValue?>(Pv("+994501234567")),    // eyni heş: dəyişməyib
                MarketingOptIn = Optional.Some(true),                       // dəyişir
            },
            Now);

        Assert.Equal(["lastName", "marketingOptIn"], result.Value);
        var e = Assert.IsType<PatientUpdated>(Assert.Single(patient.DomainEvents));
        Assert.Equal(["lastName", "marketingOptIn"], e.ChangedFields);
    }

    [Fact]
    public void Apply_with_no_effective_change_raises_no_event()
    {
        var patient = Register().Value;
        patient.ClearDomainEvents();

        var result = patient.Apply(new PatientPatch { FirstName = Optional.Some("Aysel") }, Now);

        Assert.Empty(result.Value);
        Assert.Empty(patient.DomainEvents);
    }

    [Fact]
    public void Apply_is_all_or_nothing_when_validation_fails()
    {
        var patient = Register().Value;

        var result = patient.Apply(
            new PatientPatch { FirstName = Optional.Some("Yeni"), BirthDate = Optional.Some<DateOnly?>(new DateOnly(2100, 1, 1)) },
            Now);

        Assert.True(result.IsFailure);
        Assert.Equal("Aysel", patient.FirstName);   // FirstName də dəyişmədi
    }

    [Fact]
    public void Merge_patch_null_clears_optional_fields()
    {
        var patient = Register(phone: Pv("+994501234567")).Value;

        var result = patient.Apply(new PatientPatch { Phone = Optional.Some<ProtectedValue?>(null), FatherName = Optional.Some<string?>(null) }, Now);

        Assert.Null(patient.Phone);
        Assert.Contains("phone", result.Value);
    }

    [Fact]
    public void Soft_delete_is_final()
    {
        var patient = Register().Value;
        patient.ClearDomainEvents();

        Assert.True(patient.SoftDelete(Now).IsSuccess);
        Assert.IsType<PatientDeleted>(Assert.Single(patient.DomainEvents));
        Assert.Equal(ErrorType.NotFound, patient.SoftDelete(Now).Error!.Type);
        Assert.Equal(ErrorType.NotFound, patient.Apply(new PatientPatch { FirstName = Optional.Some("X") }, Now).Error!.Type);
    }

    [Fact]
    public void Allergy_requires_substance()
    {
        Assert.True(PatientAllergy.Create(Guid.NewGuid(), Guid.NewGuid(), " ", null, AllergySeverity.Mild).IsFailure);
        var ok = PatientAllergy.Create(Guid.NewGuid(), Guid.NewGuid(), " Penisillin ", " ", AllergySeverity.Severe);
        Assert.Equal("Penisillin", ok.Value.Substance);
        Assert.Null(ok.Value.Reaction);
    }
}
