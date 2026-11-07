using DentaCore.Identity.Application;
using DentaCore.Identity.Domain;

namespace DentaCore.Identity.UnitTests;

public class CurrentUserTests
{
    private readonly FakeUsers _users = new();
    private readonly User _user = User.Register(Guid.NewGuid(), [1], [2], "hash", "Dr. Test");

    public CurrentUserTests() => _users.Items.Add(_user);

    [Fact]
    public async Task All_branches_user_gets_every_active_branch()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        _users.ActiveBranches.AddRange([a, b]);

        var result = await new GetCurrentUserQueryHandler(_users).Handle(new GetCurrentUserQuery(_user.Id), CancellationToken.None);

        Assert.Equal([a, b], result.Value.Branches);
    }

    [Fact]
    public async Task Branch_scoped_user_gets_only_own_branches_not_the_whole_clinic()
    {
        var own = Guid.NewGuid();
        _users.UserHasAllBranches = false;
        _users.UserBranchIds = [own];
        _users.ActiveBranches.Add(Guid.NewGuid());

        var result = await new GetCurrentUserQueryHandler(_users).Handle(new GetCurrentUserQuery(_user.Id), CancellationToken.None);

        Assert.Equal([own], result.Value.Branches);
    }

    [Fact]
    public async Task Unknown_user_is_not_found()
    {
        var result = await new GetCurrentUserQueryHandler(_users).Handle(new GetCurrentUserQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("user.not_found", result.Error?.Code);
    }
}
