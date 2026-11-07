using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Search.Application;

namespace DentaCore.Search.UnitTests;

public sealed class HandlerTests
{
    private sealed class FakePatientSearch(Func<IReadOnlyCollection<Guid>?, IReadOnlyList<PatientHit>>? onFind = null) : IPatientSearch
    {
        public int Calls { get; private set; }

        public IReadOnlyCollection<Guid>? Branches { get; private set; }

        public string? Query { get; private set; }

        public Task<IReadOnlyList<PatientHit>> FindAsync(string query, IReadOnlyCollection<Guid>? branches, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            Branches = branches;
            Query = query;
            return Task.FromResult(onFind?.Invoke(branches) ?? []);
        }
    }

    private sealed class FakeAuditSearch(Exception? throws = null) : IAuditSearch
    {
        public AuditFilter? Filter { get; private set; }

        public Task<AuditPage> SearchAsync(AuditFilter filter, string? cursor, int limit, CancellationToken cancellationToken)
        {
            Filter = filter;
            return throws is null ? Task.FromResult(new AuditPage([], null)) : throw throws;
        }
    }

    private static CurrentUser User(string[] perms, params Guid[] branches) => new(Guid.NewGuid(), perms, branches.Select(b => b.ToString()), null);

    // ---------------- Patient search scope ----------------
    [Fact]
    public async Task Tenant_scope_searches_everything_or_one_chosen_branch()
    {
        var search = new FakePatientSearch();
        var handler = new FindPatientsQueryHandler(search, User(["patient:read@tenant"]));
        var branch = Guid.NewGuid();

        await handler.Handle(new FindPatientsQuery("  əliyev ", null, 10), default);
        Assert.Null(search.Branches);
        Assert.Equal("əliyev", search.Query);   // kənar boşluqlar atılır

        await handler.Handle(new FindPatientsQuery("əliyev", branch, 10), default);
        Assert.Equal([branch], search.Branches);
    }

    [Fact]
    public async Task Branch_scope_is_pushed_into_the_query_and_foreign_branches_look_nonexistent()
    {
        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();
        var search = new FakePatientSearch();
        var handler = new FindPatientsQueryHandler(search, User(["patient:read@branch"], mine, other));

        await handler.Handle(new FindPatientsQuery("ab", null, 10), default);
        Assert.Equal(new[] { mine, other }.Order(), search.Branches!.Order());

        var narrowed = await handler.Handle(new FindPatientsQuery("ab", mine, 10), default);
        Assert.True(narrowed.IsSuccess);
        Assert.Equal([mine], search.Branches);

        var calls = search.Calls;
        var foreign = await handler.Handle(new FindPatientsQuery("ab", Guid.NewGuid(), 10), default);
        Assert.Equal("branch.not_found", foreign.Error!.Code);
        Assert.Equal(calls, search.Calls);   // yad filial üçün mühərrikə sorğu belə getmir
    }

    [Fact]
    public async Task Branch_scope_with_wildcard_searches_everything_and_without_branches_gets_nothing()
    {
        var search = new FakePatientSearch();
        var wildcard = new FindPatientsQueryHandler(search, new CurrentUser(Guid.NewGuid(), ["patient:read@branch"], ["*"], null));
        var empty = new FindPatientsQueryHandler(search, User(["patient:read@branch"]));

        await wildcard.Handle(new FindPatientsQuery("ab", null, 10), default);
        Assert.Null(search.Branches);

        var calls = search.Calls;
        Assert.Equal("search.no_branches", (await empty.Handle(new FindPatientsQuery("ab", null, 10), default)).Error!.Code);
        Assert.Equal(calls, search.Calls);
    }

    [Theory]
    [InlineData("patient:read@own")]
    [InlineData("invoice:read@tenant")]
    public async Task Own_scope_and_missing_permission_are_refused_before_any_search(string perm)
    {
        var search = new FakePatientSearch();

        var result = await new FindPatientsQueryHandler(search, User([perm], Guid.NewGuid())).Handle(new FindPatientsQuery("ab", null, 10), default);

        Assert.Equal("search.scope_unsupported", result.Error!.Code);
        Assert.Equal(0, search.Calls);
    }

    [Fact]
    public async Task Engine_outage_becomes_a_503_style_error_not_an_exception()
    {
        var search = new FakePatientSearch(_ => throw new SearchUnavailableException("down"));

        var result = await new FindPatientsQueryHandler(search, User(["patient:read@tenant"])).Handle(new FindPatientsQuery("ab", null, 10), default);

        Assert.Equal(ErrorType.Unavailable, result.Error!.Type);
        Assert.Equal("search.unavailable", result.Error.Code);
    }

    [Theory]
    [InlineData("a", false)]
    [InlineData("7", true)]
    [InlineData(" 7 ", true)]
    [InlineData("ab", true)]
    [InlineData("  a  ", false)]
    public void Query_validation_allows_a_single_digit_but_not_a_single_letter(string query, bool valid) =>
        Assert.Equal(valid, new FindPatientsQueryValidator().Validate(new FindPatientsQuery(query, null, 10)).IsValid);

    [Fact]
    public void Query_validation_limits_length_and_result_count()
    {
        var validator = new FindPatientsQueryValidator();

        Assert.True(validator.Validate(new FindPatientsQuery(new string('x', 100), null, 50)).IsValid);
        Assert.False(validator.Validate(new FindPatientsQuery(new string('x', 101), null, 10)).IsValid);
        Assert.False(validator.Validate(new FindPatientsQuery("ab", null, 0)).IsValid);
        Assert.False(validator.Validate(new FindPatientsQuery("ab", null, 51)).IsValid);
    }

    [Fact]
    public void Search_audit_records_the_result_count_but_never_the_query_text()
    {
        var descriptor = new FindPatientsQuery("Əliyev Rəşad", null, 10).Describe([new PatientHit(Guid.NewGuid(), 1, "Rəşad", "Əliyev", null, Guid.NewGuid(), 1)]);

        Assert.Equal("patient.search", descriptor.Action);
        Assert.DoesNotContain("Əliyev", descriptor.DetailsJson!, StringComparison.Ordinal);
        Assert.Contains("\"results\":1", descriptor.DetailsJson, StringComparison.Ordinal);
    }

    // ---------------- Audit search ----------------
    [Theory]
    [InlineData("audit:read@branch")]
    [InlineData("audit:read@own")]
    [InlineData("patient:read@tenant")]
    public async Task Audit_search_needs_tenant_scope(string perm)
    {
        var search = new FakeAuditSearch();

        var result = await new SearchAuditQueryHandler(search, User([perm], Guid.NewGuid())).Handle(new SearchAuditQuery(null, null, null, null, null, null, null, 10), default);

        Assert.Equal("audit.scope_insufficient", result.Error!.Code);
        Assert.Null(search.Filter);
    }

    [Fact]
    public async Task Audit_filters_are_passed_through_and_failures_are_mapped()
    {
        var entity = Guid.NewGuid();
        var user = Guid.NewGuid();
        var search = new FakeAuditSearch();
        var ok = await new SearchAuditQueryHandler(search, User(["audit:read@tenant"])).Handle(new SearchAuditQuery("patient", entity, user, "patient.read", null, null, null, 10), default);

        Assert.True(ok.IsSuccess);
        Assert.Equal(new AuditFilter("patient", entity, user, "patient.read", null, null), search.Filter);

        var down = await new SearchAuditQueryHandler(new FakeAuditSearch(new SearchUnavailableException("x")), User(["audit:read@tenant"])).Handle(new SearchAuditQuery(null, null, null, null, null, null, null, 10), default);
        var badCursor = await new SearchAuditQueryHandler(new FakeAuditSearch(new FormatException()), User(["audit:read@tenant"])).Handle(new SearchAuditQuery(null, null, null, null, null, null, "zzz", 10), default);
        Assert.Equal("search.unavailable", down.Error!.Code);
        Assert.Equal("audit.invalid_cursor", badCursor.Error!.Code);
    }

    [Fact]
    public void Audit_validator_checks_limit_and_time_order()
    {
        var validator = new SearchAuditQueryValidator();
        var now = DateTimeOffset.UtcNow;

        Assert.True(validator.Validate(new SearchAuditQuery(null, null, null, null, now.AddDays(-1), now, null, 100)).IsValid);
        Assert.False(validator.Validate(new SearchAuditQuery(null, null, null, null, null, null, null, 101)).IsValid);
        Assert.False(validator.Validate(new SearchAuditQuery(null, null, null, null, null, null, null, 0)).IsValid);
        Assert.False(validator.Validate(new SearchAuditQuery(null, null, null, null, now, now.AddDays(-1), null, 10)).IsValid);
    }
}
