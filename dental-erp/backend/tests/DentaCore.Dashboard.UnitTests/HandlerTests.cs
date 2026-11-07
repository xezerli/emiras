using DentaCore.BuildingBlocks.Application;
using DentaCore.Dashboard.Application;
using Microsoft.Extensions.Options;

namespace DentaCore.Dashboard.UnitTests;

public sealed class HandlerTests
{
    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class RecordingReader : IDashboardReader
    {
        public IReadOnlyCollection<Guid>? Branches { get; private set; }

        public DateOnly Date { get; private set; }

        public int Calls { get; private set; }

        public Task<DashboardSummaryDto> ReadAsync(IReadOnlyCollection<Guid>? branches, DateOnly date, CancellationToken cancellationToken)
        {
            Calls++;
            Branches = branches;
            Date = date;
            return Task.FromResult(new DashboardSummaryDto(date, new AppointmentsToday(0, 0, 0, 0), 0, 0, 0, 0, [], [], [], []));
        }
    }

    // 2026-03-10 21:30 UTC = 2026-03-11 01:30 Bakı vaxtı
    private static readonly FakeClock Clock = new(new DateTimeOffset(2026, 3, 10, 21, 30, 0, TimeSpan.Zero));

    private static (GetDashboardSummaryQueryHandler Handler, RecordingReader Reader) Create(string[] perms, Guid[] branches)
    {
        var reader = new RecordingReader();
        var user = new CurrentUser(Guid.NewGuid(), perms, branches.Select(b => b.ToString()), null);
        return (new GetDashboardSummaryQueryHandler(reader, user, Clock, Options.Create(new DashboardOptions { TimeZone = "Asia/Baku" })), reader);
    }

    [Fact]
    public async Task Tenant_scope_without_a_branch_reads_everything_and_defaults_to_the_clinics_local_today()
    {
        var (handler, reader) = Create(["report:read@tenant"], []);

        var result = await handler.Handle(new GetDashboardSummaryQuery(null, null), default);

        Assert.True(result.IsSuccess);
        Assert.Null(reader.Branches);
        Assert.Equal(new DateOnly(2026, 3, 11), reader.Date);   // UTC-də hələ 10 mart, Bakıda artıq 11 mart
    }

    [Fact]
    public async Task Tenant_scope_can_narrow_to_one_branch()
    {
        var branch = Guid.NewGuid();
        var (handler, reader) = Create(["report:read@tenant"], []);

        await handler.Handle(new GetDashboardSummaryQuery(branch, null), default);

        Assert.Equal([branch], reader.Branches);
    }

    [Fact]
    public async Task Branch_scope_is_limited_to_the_users_branches_by_default()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (handler, reader) = Create(["report:read@branch"], [a, b]);

        await handler.Handle(new GetDashboardSummaryQuery(null, null), default);

        Assert.Equal(new[] { a, b }.Order(), reader.Branches!.Order());
    }

    [Fact]
    public async Task Branch_scope_cannot_ask_for_a_foreign_branch_and_learns_nothing_about_it()
    {
        var mine = Guid.NewGuid();
        var (handler, reader) = Create(["report:read@branch"], [mine]);

        var result = await handler.Handle(new GetDashboardSummaryQuery(Guid.NewGuid(), null), default);

        Assert.Equal("branch.not_found", result.Error!.Code);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Branch_scope_may_narrow_to_one_of_its_own_branches()
    {
        var a = Guid.NewGuid();
        var (handler, reader) = Create(["report:read@branch"], [a, Guid.NewGuid()]);

        await handler.Handle(new GetDashboardSummaryQuery(a, null), default);

        Assert.Equal([a], reader.Branches);
    }

    [Fact]
    public async Task Branch_scope_with_all_branches_wildcard_reads_everything()
    {
        var reader = new RecordingReader();
        var user = new CurrentUser(Guid.NewGuid(), ["report:read@branch"], ["*"], null);
        var handler = new GetDashboardSummaryQueryHandler(reader, user, Clock, Options.Create(new DashboardOptions()));

        await handler.Handle(new GetDashboardSummaryQuery(null, null), default);

        Assert.Null(reader.Branches);
    }

    [Fact]
    public async Task Branch_scope_without_any_branch_gets_nothing()
    {
        var (handler, reader) = Create(["report:read@branch"], []);

        var result = await handler.Handle(new GetDashboardSummaryQuery(null, null), default);

        Assert.Equal("dashboard.no_branches", result.Error!.Code);
        Assert.Equal(0, reader.Calls);
    }

    [Theory]
    [InlineData("report:read@own")]
    [InlineData("patient:read@tenant")]
    public async Task Own_scope_or_missing_permission_is_forbidden(string perm)
    {
        var (handler, reader) = Create([perm], [Guid.NewGuid()]);

        var result = await handler.Handle(new GetDashboardSummaryQuery(null, null), default);

        Assert.Equal("dashboard.scope_insufficient", result.Error!.Code);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Future_dates_are_refused_and_today_and_past_are_accepted()
    {
        var (handler, _) = Create(["report:read@tenant"], []);

        Assert.Equal("dashboard.future_date", (await handler.Handle(new GetDashboardSummaryQuery(null, new DateOnly(2026, 3, 12)), default)).Error!.Code);
        Assert.True((await handler.Handle(new GetDashboardSummaryQuery(null, new DateOnly(2026, 3, 11)), default)).IsSuccess);
        Assert.True((await handler.Handle(new GetDashboardSummaryQuery(null, new DateOnly(2020, 1, 1)), default)).IsSuccess);
    }
}
