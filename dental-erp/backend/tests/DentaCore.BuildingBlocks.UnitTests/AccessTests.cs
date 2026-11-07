using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.BuildingBlocks.UnitTests;

public class CurrentUserTests
{
    private static readonly Guid BranchA = Guid.NewGuid();
    private static readonly Guid BranchB = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();

    private static CurrentUser User(string[] perms, string[] branches) => new(Me, perms, branches, null);

    [Fact]
    public void Tenant_scope_sees_every_branch()
    {
        var user = User(["patient:read@tenant"], [BranchA.ToString()]);

        Assert.True(user.CanAccess("patient:read", BranchB));
        Assert.Null(user.BranchFilterFor("patient:read"));
    }

    [Fact]
    public void Branch_scope_is_limited_to_own_branches()
    {
        var user = User(["patient:read@branch"], [BranchA.ToString()]);

        Assert.True(user.CanAccess("patient:read", BranchA));
        Assert.False(user.CanAccess("patient:read", BranchB));
        Assert.Equal([BranchA], user.BranchFilterFor("patient:read"));
    }

    [Fact]
    public void Wildcard_branch_claim_means_all_branches()
    {
        var user = User(["patient:read@branch"], ["*"]);

        Assert.True(user.CanAccess("patient:read", BranchB));
        Assert.Null(user.BranchFilterFor("patient:read"));
    }

    [Fact]
    public void Own_scope_requires_matching_owner()
    {
        var user = User(["patient:read@own"], ["*"]);

        Assert.True(user.CanAccess("patient:read", BranchA, Me));
        Assert.False(user.CanAccess("patient:read", BranchA, Guid.NewGuid()));
        Assert.False(user.CanAccess("patient:read", BranchA, null));
    }

    [Fact]
    public void Missing_permission_denies_everything_and_empty_filter()
    {
        var user = User(["patient:read@tenant"], ["*"]);

        Assert.False(user.CanAccess("invoice:refund", BranchA, Me));
        Assert.Empty(user.BranchFilterFor("invoice:refund")!);
    }

    [Fact]
    public void Widest_scope_wins_when_permission_is_repeated()
    {
        var user = User(["patient:read@own", "patient:read@tenant", "patient:read@branch"], ["*"]);

        Assert.Equal(PermissionScope.Tenant, user.ScopeOf("patient:read"));
    }

    [Theory]
    [InlineData("patient:read")]
    [InlineData("patient:read@galaxy")]
    [InlineData("@tenant")]
    public void Malformed_permission_claims_are_ignored(string claim)
    {
        var user = User([claim], ["*"]);

        Assert.False(user.CanAccess("patient:read", BranchA, Me));
    }

    [Fact]
    public void Anonymous_user_is_not_authenticated()
    {
        Assert.False(CurrentUser.Anonymous.IsAuthenticated);
        Assert.False(CurrentUser.Anonymous.CanAccess("patient:read", BranchA, Me));
    }
}

public class AuthorizationAndAuditBehaviorTests
{
    private sealed record Req(bool Guarded = true) : IQuery<int>, IRequiresAccess, IAuditable<int>
    {
        public string Permission => "patient:read";

        public AuditDescriptor Describe(int response) => new("patient.read", "patient", Guid.Empty, $"{{\"n\":{response}}}");
    }

    private sealed class Trail : IAuditTrail
    {
        public List<AuditRecord> Records { get; } = [];

        public Task RecordAsync(AuditRecord record, CancellationToken cancellationToken)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
    }

    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task Unauthenticated_request_is_rejected_with_401_error()
    {
        var behavior = new AuthorizationBehavior<Req, int>(CurrentUser.Anonymous, [new Trail()], new Clock());

        var result = await behavior.Handle(new Req(), () => Task.FromResult(Result.Success(1)), CancellationToken.None);

        Assert.Equal(ErrorType.Unauthorized, result.Error!.Type);
    }

    [Fact]
    public async Task Missing_permission_is_forbidden_and_the_attempt_is_audited_without_calling_handler()
    {
        var trail = new Trail();
        var user = new CurrentUser(UserId, ["invoice:read@tenant"], ["*"], null);
        var behavior = new AuthorizationBehavior<Req, int>(user, [trail], new Clock());
        var called = false;

        var result = await behavior.Handle(new Req(), () => { called = true; return Task.FromResult(Result.Success(1)); }, CancellationToken.None);

        Assert.Equal(ErrorType.Forbidden, result.Error!.Type);
        Assert.False(called);
        Assert.Equal("permission.denied", Assert.Single(trail.Records).Action);
    }

    [Fact]
    public async Task Granted_permission_reaches_handler()
    {
        var user = new CurrentUser(UserId, ["patient:read@branch"], ["*"], null);
        var behavior = new AuthorizationBehavior<Req, int>(user, [new Trail()], new Clock());

        var result = await behavior.Handle(new Req(), () => Task.FromResult(Result.Success(7)), CancellationToken.None);

        Assert.Equal(7, result.Value);
    }

    [Fact]
    public async Task Successful_auditable_request_is_recorded_with_user_and_descriptor()
    {
        var trail = new Trail();
        var user = new CurrentUser(UserId, ["patient:read@tenant"], ["*"], System.Net.IPAddress.Parse("10.0.0.5"));
        var behavior = new AuditBehavior<Req, int>(user, [trail], new Clock());

        await behavior.Handle(new Req(), () => Task.FromResult(Result.Success(3)), CancellationToken.None);

        var record = Assert.Single(trail.Records);
        Assert.Equal("patient.read", record.Action);
        Assert.Equal(UserId, record.UserId);
        Assert.Equal("10.0.0.5", record.Ip);
        Assert.Equal("{\"n\":3}", record.DetailsJson);
    }

    [Fact]
    public async Task Failed_request_is_not_recorded_as_a_successful_access()
    {
        var trail = new Trail();
        var behavior = new AuditBehavior<Req, int>(new CurrentUser(UserId, [], [], null), [trail], new Clock());

        await behavior.Handle(new Req(), () => Task.FromResult<Result<int>>(Error.NotFound("x", "y")), CancellationToken.None);

        Assert.Empty(trail.Records);
    }

    [Fact]
    public async Task Auditable_request_without_registered_trail_fails_closed()
    {
        var behavior = new AuditBehavior<Req, int>(new CurrentUser(UserId, [], [], null), [], new Clock());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            behavior.Handle(new Req(), () => Task.FromResult(Result.Success(1)), CancellationToken.None));
    }
}
