using DentaCore.BuildingBlocks.Domain;
using DentaCore.Identity.Application;
using DentaCore.Identity.Domain;
using DentaCore.Identity.Infrastructure.Security;

namespace DentaCore.Identity.UnitTests;

public class RefreshHandlerTests
{
    private readonly FakeUsers _users = new();
    private readonly FakeRefreshTokens _tokens = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly RefreshTokenFactory _factory = new();
    private readonly User _user = User.Register(Guid.NewGuid(), [1], [2], "hash", "Dr. Test");
    private readonly Guid _family = Guid.NewGuid();

    public RefreshHandlerTests() => _users.Items.Add(_user);

    private RefreshTokenCommandHandler Handler() =>
        new(_users, _tokens, new FakeTokenIssuer(), _factory, new FakeTenant(), _uow, _clock);

    private string SeedToken(TimeSpan? lifetime = null)
    {
        var (raw, hash) = _factory.Create();
        _tokens.Items.Add(RefreshToken.Issue(Guid.NewGuid(), _user.Id, _family, hash, _clock.UtcNow, lifetime ?? TimeSpan.FromDays(7), null));
        return raw;
    }

    [Fact]
    public async Task Valid_token_rotates_within_same_family_in_one_transaction()
    {
        var raw = SeedToken();

        var result = await Handler().Handle(new RefreshTokenCommand(raw, Ip.Office), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotEqual(raw, result.Value.RefreshToken);
        Assert.Equal(2, _tokens.Items.Count);
        Assert.All(_tokens.Items, t => Assert.Equal(_family, t.FamilyId));
        Assert.Single(_tokens.Consumed);
        Assert.Equal(1, _uow.Commits);
    }

    [Fact]
    public async Task Unknown_token_is_unauthorized()
    {
        var result = await Handler().Handle(new RefreshTokenCommand("garbage", Ip.Office), CancellationToken.None);

        Assert.Equal("auth.invalid_refresh_token", result.Error!.Code);
        Assert.Empty(_tokens.RevokedFamilies);
    }

    [Fact]
    public async Task Expired_token_is_rejected_without_revoking_family()
    {
        var raw = SeedToken(TimeSpan.FromMinutes(1));
        _clock.UtcNow = _clock.UtcNow.AddMinutes(2);

        var result = await Handler().Handle(new RefreshTokenCommand(raw, Ip.Office), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Empty(_tokens.RevokedFamilies);
    }

    [Fact]
    public async Task Reusing_a_consumed_token_revokes_the_whole_family_and_raises_event()
    {
        var raw = SeedToken();
        var first = await Handler().Handle(new RefreshTokenCommand(raw, Ip.Office), CancellationToken.None);
        Assert.True(first.IsSuccess);

        // Köhnə token yenidən təqdim olunur. Əslində TryConsume-a qədər IsConsumedOrRevoked yoxlanır:
        // real DB-də used_at doludur, fake-də eyni nəticəni Consumed ilə təqlid edirik.
        _tokens.LoseConsumeRace = true;
        var replay = await Handler().Handle(new RefreshTokenCommand(raw, Ip.Office), CancellationToken.None);

        Assert.True(replay.IsFailure);
        Assert.Contains(_family, _tokens.RevokedFamilies);
        Assert.Contains(_tokens.Items.SelectMany(t => t.DomainEvents), e => e is RefreshTokenReuseDetected);
    }

    [Fact]
    public async Task Losing_the_concurrent_consume_race_is_treated_as_reuse()
    {
        var raw = SeedToken();
        _tokens.LoseConsumeRace = true;

        var result = await Handler().Handle(new RefreshTokenCommand(raw, Ip.Office), CancellationToken.None);

        Assert.Equal(ErrorType.Unauthorized, result.Error!.Type);
        Assert.Contains(_family, _tokens.RevokedFamilies);
        Assert.Equal(0, _uow.Commits);   // tranzaksiya commit olunmayıb, yeni token verilməyib
        Assert.Single(_tokens.Items);
    }

    [Fact]
    public async Task Repeated_replay_of_an_already_revoked_family_does_not_raise_more_events()
    {
        var raw = SeedToken();
        _tokens.LoseConsumeRace = true;

        await Handler().Handle(new RefreshTokenCommand(raw, Ip.Office), CancellationToken.None);
        await Handler().Handle(new RefreshTokenCommand(raw, Ip.Office), CancellationToken.None);

        Assert.Single(_tokens.Items.SelectMany(t => t.DomainEvents), e => e is RefreshTokenReuseDetected);
    }

    [Fact]
    public async Task Disabled_user_cannot_refresh()
    {
        var raw = SeedToken();
        _user.Disable();

        var result = await Handler().Handle(new RefreshTokenCommand(raw, Ip.Office), CancellationToken.None);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public async Task Logout_revokes_family_and_is_idempotent_for_unknown_tokens()
    {
        var raw = SeedToken();
        var handler = new LogoutCommandHandler(_tokens, _factory, _clock);

        var ok = await handler.Handle(new LogoutCommand(raw), CancellationToken.None);
        var unknown = await handler.Handle(new LogoutCommand("nope"), CancellationToken.None);

        Assert.True(ok.IsSuccess);
        Assert.True(unknown.IsSuccess);
        Assert.Contains(_family, _tokens.RevokedFamilies);
    }
}
