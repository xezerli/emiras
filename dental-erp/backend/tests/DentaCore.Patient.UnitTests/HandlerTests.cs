using System.Text.Json;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Patient.Application;

namespace DentaCore.Patient.UnitTests;

public class RegisterAndGetHandlerTests
{
    private readonly FakePatients _patients = new();
    private readonly FakePatientUow _uow = new();
    private readonly DentaCore.BuildingBlocks.Infrastructure.Security.AesGcmPiiProtector _pii = Factory.Pii();
    private readonly Guid _me = Guid.NewGuid();

    private RegisterPatientCommandHandler Register(CurrentUser user) => new(_patients, _pii, user, _uow, Factory.Opts(), new FakeClock());

    private static RegisterPatientCommand Cmd(string? phone = "0501234567", Guid? branch = null, bool confirm = false) =>
        new(branch ?? Factory.BranchA, "Aysel", "Qasımova", null, new DateOnly(1990, 5, 1), "F", phone, "Aysel@Clinic.az", "aze1234567", null, null, false, null, confirm);

    [Fact]
    public async Task Register_encrypts_contacts_and_returns_masked_dto()
    {
        var user = Factory.User(_me, ["patient:write@branch"], [Factory.BranchA.ToString()]);

        var result = await Register(user).Handle(Cmd(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var stored = Assert.Single(_patients.Items);
        Assert.Equal("+994501234567", _pii.Decrypt(stored.Phone!.Cipher));
        Assert.Equal("aysel@clinic.az", _pii.Decrypt(stored.Email!.Cipher));
        Assert.Equal("AZE1234567", _pii.Decrypt(stored.NationalId!.Cipher));
        Assert.Equal("+994*******67", result.Value.Phone);   // cavabda maskalanıb
        Assert.False(result.Value.SensitiveRevealed);
        Assert.Equal(1, _uow.Saves);
    }

    [Fact]
    public async Task Registering_outside_permitted_branch_is_forbidden()
    {
        var user = Factory.User(_me, ["patient:write@branch"], [Factory.BranchA.ToString()]);

        var result = await Register(user).Handle(Cmd(branch: Factory.BranchB), CancellationToken.None);

        Assert.Equal(ErrorType.Forbidden, result.Error!.Type);
        Assert.Empty(_patients.Items);
    }

    [Fact]
    public async Task Duplicate_phone_is_a_conflict_and_reveals_existing_id_only_when_accessible()
    {
        var owner = Factory.User(_me, ["patient:write@tenant", "patient:read@tenant"]);
        await Register(owner).Handle(Cmd(branch: Factory.BranchB), CancellationToken.None);
        var existingId = _patients.Items[0].Id;

        // Eyni nömrə, fərqli yazılış. Tenant scope: id göstərilir
        var tenantUser = Factory.User(Guid.NewGuid(), ["patient:write@tenant", "patient:read@tenant"]);
        var withAccess = await Register(tenantUser).Handle(Cmd("+994 50 123 45 67"), CancellationToken.None);
        // Yalnız A filialı: B filialındakı pasiyentin id-si açıqlanmır
        var branchUser = Factory.User(Guid.NewGuid(), ["patient:write@branch", "patient:read@branch"], [Factory.BranchA.ToString()]);
        var withoutAccess = await Register(branchUser).Handle(Cmd("+994 50 123 45 67"), CancellationToken.None);

        Assert.Equal("patient.duplicate", withAccess.Error!.Code);
        Assert.Equal(existingId, withAccess.Error.Details!["existingPatientId"]);
        Assert.Equal("patient.duplicate", withoutAccess.Error!.Code);
        Assert.Null(withoutAccess.Error.Details);
        Assert.Single(_patients.Items);
    }

    [Fact]
    public async Task Confirmed_duplicate_is_allowed_for_families_sharing_a_phone()
    {
        var user = Factory.User(_me, ["patient:write@tenant", "patient:read@tenant"]);
        await Register(user).Handle(Cmd(), CancellationToken.None);

        var second = await Register(user).Handle(Cmd(confirm: true) with { NationalId = null }, CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.Equal(2, _patients.Items.Count);
    }

    [Fact]
    public async Task Invalid_phone_is_a_validation_error()
    {
        var user = Factory.User(_me, ["patient:write@tenant"]);

        var result = await Register(user).Handle(Cmd("12"), CancellationToken.None);

        Assert.Equal("patient.invalid_phone", result.Error!.Code);
    }

    private async Task<Domain.Patient> Seed(Guid branch, Guid creator)
    {
        var user = Factory.User(creator, ["patient:write@tenant"]);
        var result = await Register(user).Handle(Cmd(branch: branch, confirm: true), CancellationToken.None);
        Assert.True(result.IsSuccess);
        return _patients.Items[^1];
    }

    [Fact]
    public async Task Get_outside_scope_looks_like_not_found()
    {
        var patient = await Seed(Factory.BranchB, _me);
        var handler = new GetPatientQueryHandler(_patients, _pii, Factory.User(Guid.NewGuid(), ["patient:read@branch"], [Factory.BranchA.ToString()]));

        var result = await handler.Handle(new GetPatientQuery(patient.Id, false), CancellationToken.None);

        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
    }

    [Fact]
    public async Task Reveal_needs_sensitive_permission_and_returns_plaintext_only_then()
    {
        var patient = await Seed(Factory.BranchA, _me);
        var plain = new GetPatientQueryHandler(_patients, _pii, Factory.User(Guid.NewGuid(), ["patient:read@tenant"]));
        var privileged = new GetPatientQueryHandler(_patients, _pii, Factory.User(Guid.NewGuid(), ["patient:read@tenant", "patient:read_sensitive@tenant"]));

        var denied = await plain.Handle(new GetPatientQuery(patient.Id, true), CancellationToken.None);
        var masked = await plain.Handle(new GetPatientQuery(patient.Id, false), CancellationToken.None);
        var revealed = await privileged.Handle(new GetPatientQuery(patient.Id, true), CancellationToken.None);

        Assert.Equal(ErrorType.Forbidden, denied.Error!.Type);
        Assert.Equal("+994*******67", masked.Value.Phone);
        Assert.Equal("+994501234567", revealed.Value.Phone);
        Assert.Equal("AZE1234567", revealed.Value.NationalId);
        Assert.True(revealed.Value.SensitiveRevealed);
    }

    [Fact]
    public async Task Own_scope_user_sees_only_patients_they_created()
    {
        var mine = await Seed(Factory.BranchA, _me);
        var theirs = await Seed(Factory.BranchA, Guid.NewGuid());
        var handler = new GetPatientQueryHandler(_patients, _pii, Factory.User(_me, ["patient:read@own"]));

        Assert.True((await handler.Handle(new GetPatientQuery(mine.Id, false), CancellationToken.None)).IsSuccess);
        Assert.Equal(ErrorType.NotFound, (await handler.Handle(new GetPatientQuery(theirs.Id, false), CancellationToken.None)).Error!.Type);
    }

    [Fact]
    public async Task Audit_descriptor_distinguishes_sensitive_reads_and_hides_search_text()
    {
        Assert.Equal("patient.read", new GetPatientQuery(Guid.NewGuid(), false).Describe(null!).Action);
        Assert.Equal("patient.read.sensitive", new GetPatientQuery(Guid.NewGuid(), true).Describe(null!).Action);

        var descriptor = new SearchPatientsQuery("Aysel 0501234567", null, null).Describe(new PatientPage([], null));

        Assert.DoesNotContain("Aysel", descriptor.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("050", descriptor.DetailsJson, StringComparison.Ordinal);
        await Task.CompletedTask;
    }
}

public class UpdateHandlerTests
{
    private readonly FakePatients _patients = new();
    private readonly FakePatientUow _uow = new();
    private readonly DentaCore.BuildingBlocks.Infrastructure.Security.AesGcmPiiProtector _pii = Factory.Pii();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Domain.Patient _patient;

    public UpdateHandlerTests()
    {
        var phone = new Domain.ProtectedValue(_pii.Encrypt("+994501234567"), _pii.BlindIndex("+994501234567"));
        _patient = Domain.Patient.Register(Guid.NewGuid(), Factory.BranchA, "Aysel", "Qasımova", "Ələsgər", null, 'F', null, phone, null, null, "sms", false, null, _me, new FakeClock().UtcNow).Value;
        _patients.Items.Add(_patient);
    }

    private UpdatePatientCommandHandler Handler(params string[] perms) =>
        new(_patients, _pii, Factory.User(_me, perms.Length == 0 ? ["patient:write@tenant"] : perms), Factory.Opts(), new FakeClock(), _uow);

    private static Dictionary<string, JsonElement> Patch(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task Stale_version_gets_412_before_anything_changes()
    {
        var result = await Handler().Handle(new UpdatePatientCommand(_patient.Id, 99, Patch("""{"firstName":"X"}""")), CancellationToken.None);

        Assert.Equal(ErrorType.PreconditionFailed, result.Error!.Type);
        Assert.Equal("Aysel", _patient.FirstName);
    }

    [Fact]
    public async Task Patch_updates_fields_and_null_clears()
    {
        var result = await Handler().Handle(
            new UpdatePatientCommand(_patient.Id, _patient.RowVersion, Patch("""{"lastName":"Əliyeva","fatherName":null,"birthDate":"1991-02-03","phone":"0551112233"}""")),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Əliyeva", _patient.LastName);
        Assert.Null(_patient.FatherName);
        Assert.Equal(new DateOnly(1991, 2, 3), _patient.BirthDate);
        Assert.Equal("+994551112233", _pii.Decrypt(_patient.Phone!.Cipher));
        Assert.Equal(["lastName", "fatherName", "birthDate", "phone"], result.Value.ChangedFields);
    }

    [Theory]
    [InlineData("""{"chartNo":5}""", "patch.unknown_field")]
    [InlineData("""{"id":"x"}""", "patch.unknown_field")]
    [InlineData("""{"firstName":null}""", "patch.invalid_value")]
    [InlineData("""{"firstName":5}""", "patch.invalid_value")]
    [InlineData("""{"birthDate":"03.02.1991"}""", "patch.invalid_value")]
    [InlineData("""{"marketingOptIn":"yes"}""", "patch.invalid_value")]
    [InlineData("""{"phone":"12"}""", "patient.invalid_phone")]
    [InlineData("""{"email":"nope"}""", "patient.invalid_email")]
    [InlineData("""{"gender":"X"}""", "patient.invalid_gender")]
    [InlineData("""{"address":"flat"}""", "patch.invalid_value")]
    public async Task Bad_patch_is_rejected_with_a_specific_code(string json, string code)
    {
        var result = await Handler().Handle(new UpdatePatientCommand(_patient.Id, _patient.RowVersion, Patch(json)), CancellationToken.None);

        Assert.Equal(code, result.Error!.Code);
        Assert.Equal("Aysel", _patient.FirstName);
    }

    [Fact]
    public async Task Changing_phone_to_another_patients_number_is_a_duplicate()
    {
        var other = Domain.Patient.Register(
            Guid.NewGuid(), Factory.BranchA, "Elnur", "Vəliyev", null, null, 'M', null,
            new Domain.ProtectedValue(_pii.Encrypt("+994551112233"), _pii.BlindIndex("+994551112233")), null, null, "sms", false, null, _me, new FakeClock().UtcNow).Value;
        _patients.Items.Add(other);

        var result = await Handler().Handle(new UpdatePatientCommand(_patient.Id, _patient.RowVersion, Patch("""{"phone":"055 111 22 33"}""")), CancellationToken.None);

        Assert.Equal("patient.duplicate", result.Error!.Code);
    }

    [Fact]
    public async Task Writing_without_scope_is_not_found()
    {
        var handler = new UpdatePatientCommandHandler(_patients, _pii, Factory.User(Guid.NewGuid(), ["patient:write@branch"], [Factory.BranchB.ToString()]), Factory.Opts(), new FakeClock(), _uow);

        var result = await handler.Handle(new UpdatePatientCommand(_patient.Id, _patient.RowVersion, Patch("""{"firstName":"X"}""")), CancellationToken.None);

        Assert.Equal(ErrorType.NotFound, result.Error!.Type);
    }

    [Fact]
    public async Task Delete_is_soft_and_scoped()
    {
        var denied = new DeletePatientCommandHandler(_patients, Factory.User(Guid.NewGuid(), ["patient:write@branch"], [Factory.BranchB.ToString()]), new FakeClock());
        var allowed = new DeletePatientCommandHandler(_patients, Factory.User(_me, ["patient:write@tenant"]), new FakeClock());

        Assert.Equal(ErrorType.NotFound, (await denied.Handle(new DeletePatientCommand(_patient.Id), CancellationToken.None)).Error!.Type);
        Assert.True((await allowed.Handle(new DeletePatientCommand(_patient.Id), CancellationToken.None)).IsSuccess);
        Assert.NotNull(_patient.DeletedAt);
        Assert.Equal(ErrorType.NotFound, (await allowed.Handle(new DeletePatientCommand(_patient.Id), CancellationToken.None)).Error!.Type);
    }
}

public class SearchHandlerTests
{
    private readonly FakeReadModel _read = new();
    private readonly DentaCore.BuildingBlocks.Infrastructure.Security.AesGcmPiiProtector _pii = Factory.Pii();

    private SearchPatientsQueryHandler Handler(CurrentUser user) => new(_read, _pii, user, Factory.Opts());

    private static CurrentUser User(params string[] perms) => Factory.User(Guid.NewGuid(), perms, [Factory.BranchA.ToString()]);

    [Fact]
    public async Task Name_text_becomes_tokens_and_digits_become_phone_and_chart_number()
    {
        await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery("Əliyev Aysel", null, null), CancellationToken.None);
        Assert.Equal(["Əliyev", "Aysel"], _read.LastCriteria!.NameTokens);
        Assert.Null(_read.LastCriteria.PhoneHash);

        await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery("050 123 45 67", null, null), CancellationToken.None);
        Assert.Empty(_read.LastCriteria!.NameTokens);
        Assert.Equal(_pii.BlindIndex("+994501234567"), _read.LastCriteria.PhoneHash);

        await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery("004281", null, null), CancellationToken.None);
        Assert.Equal(4281, _read.LastCriteria!.ChartNo);
        Assert.Null(_read.LastCriteria.PhoneHash);
    }

    [Fact]
    public async Task Single_alphanumeric_token_is_also_tried_as_national_id()
    {
        await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery("aze1234567", null, null), CancellationToken.None);

        Assert.Equal(_pii.BlindIndex("AZE1234567"), _read.LastCriteria!.NationalIdHash);
    }

    [Fact]
    public async Task Scope_is_translated_into_query_filters()
    {
        await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery(null, null, null), CancellationToken.None);
        Assert.Null(_read.LastCriteria!.AllowedBranchIds);
        Assert.Null(_read.LastCriteria.OwnerUserId);

        await Handler(User("patient:read@branch")).Handle(new SearchPatientsQuery(null, null, null), CancellationToken.None);
        Assert.Equal([Factory.BranchA], _read.LastCriteria!.AllowedBranchIds);

        var me = Guid.NewGuid();
        await Handler(Factory.User(me, ["patient:read@own"])).Handle(new SearchPatientsQuery(null, null, null), CancellationToken.None);
        Assert.Equal(me, _read.LastCriteria!.OwnerUserId);
    }

    [Fact]
    public async Task Paging_asks_one_extra_row_and_returns_a_cursor_that_round_trips()
    {
        for (var i = 0; i < 3; i++)
        {
            _read.Rows.Add(new PatientListRow(Guid.NewGuid(), i + 1, Factory.BranchA, "A", "B", null, _pii.Encrypt("+994501234567"), new DateTimeOffset(2026, 10, 7, 9, i, 0, TimeSpan.Zero)));
        }

        var page = await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery(null, null, null, 2), CancellationToken.None);

        Assert.Equal(3, _read.LastCriteria!.Limit);   // 2 + 1
        Assert.Equal(2, page.Value.Items.Count);
        Assert.NotNull(page.Value.NextCursor);
        Assert.Equal("+994*******67", page.Value.Items[0].PhoneMasked);

        await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery(null, null, page.Value.NextCursor, 2), CancellationToken.None);
        Assert.Equal(_read.Rows[1].Id, _read.LastCriteria!.After!.Value.Id);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    [InlineData("!!!")]
    public async Task Unrecognizable_text_returns_nothing_instead_of_everything(string text)
    {
        _read.Rows.Add(new PatientListRow(Guid.NewGuid(), 1, Factory.BranchA, "A", "B", null, null, DateTimeOffset.UtcNow));

        var page = await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery(text, null, null), CancellationToken.None);

        Assert.Empty(page.Value.Items);
        Assert.Null(_read.LastCriteria);   // DB-yə sorğu belə getmir
    }

    [Fact]
    public async Task Garbage_cursor_is_a_validation_error()
    {
        var result = await Handler(User("patient:read@tenant")).Handle(new SearchPatientsQuery(null, null, "!!!not-base64", 10), CancellationToken.None);

        Assert.Equal("cursor.invalid", result.Error!.Code);
    }
}
