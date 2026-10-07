using System.Net;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Identity.Application;
using DentaCore.Identity.Domain;

namespace DentaCore.Identity.UnitTests;

public class LoginHandlerTests
{
    private const string Email = "nermin@clinic.az";
    private const string Password = "S3cret!pass";

    private readonly FakeUsers _users = new();
    private readonly FakeRefreshTokens _tokens = new();
    private readonly FakeHasher _hasher = new();
    private readonly FakeUnitOfWork _uow = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero));
    private readonly DentaCore.BuildingBlocks.Infrastructure.Security.AesGcmPiiProtector _pii = TestPii.Create();
    private readonly User _user;

    public LoginHandlerTests()
    {
        _user = User.Register(Guid.NewGuid(), _pii.BlindIndex(Email), _pii.Encrypt(Email), _hasher.Hash(Password), "Nərmin Əliyeva");
        _users.Items.Add(_user);
    }

    private LoginCommandHandler Handler() =>
        new(_users, _tokens, _hasher, _pii, new FakeTokenIssuer(), new DentaCore.Identity.Infrastructure.Security.RefreshTokenFactory(), new FakeTenant(), _uow, _clock);

    [Fact]
    public async Task Valid_credentials_issue_tokens_and_store_hashed_refresh_token()
    {
        var result = await Handler().Handle(new LoginCommand(Email, Password, Ip.Office), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var tokens = result.Value.Tokens!;
        Assert.StartsWith("access-for-", tokens.AccessToken, StringComparison.Ordinal);
        var stored = Assert.Single(_tokens.Items);
        // Açıq token DB-yə düşmür, yalnız heş
        Assert.NotEqual(System.Text.Encoding.UTF8.GetBytes(tokens.RefreshToken), stored.TokenHash);
        Assert.Equal(_user.Id, stored.UserId);
        Assert.Contains(_user.DomainEvents, e => e is UserLoggedIn);
    }

    [Fact]
    public async Task Email_lookup_is_case_insensitive()
    {
        var result = await Handler().Handle(new LoginCommand("  NERMIN@Clinic.az ", Password, Ip.Office), CancellationToken.None);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Unknown_email_returns_same_error_as_wrong_password_and_burns_time()
    {
        var unknown = await Handler().Handle(new LoginCommand("nobody@clinic.az", Password, Ip.Office), CancellationToken.None);
        var wrong = await Handler().Handle(new LoginCommand(Email, "wrong", Ip.Office), CancellationToken.None);

        Assert.Equal(unknown.Error, wrong.Error);
        Assert.Equal(ErrorType.Unauthorized, unknown.Error!.Type);
        Assert.Equal(1, _hasher.BurnCount);
    }

    [Fact]
    public async Task Wrong_password_persists_failure_counter_even_though_result_is_failure()
    {
        await Handler().Handle(new LoginCommand(Email, "wrong", Ip.Office), CancellationToken.None);

        Assert.Equal(1, _user.FailedLogins);
        Assert.Equal(1, _uow.Saves);
    }

    [Fact]
    public async Task Account_locks_after_five_failures_and_rejects_even_correct_password()
    {
        for (var i = 0; i < 5; i++)
        {
            await Handler().Handle(new LoginCommand(Email, "wrong", Ip.Office), CancellationToken.None);
        }

        var result = await Handler().Handle(new LoginCommand(Email, Password, Ip.Office), CancellationToken.None);

        Assert.Equal("auth.account_locked", result.Error!.Code);
        Assert.Equal(ErrorType.Locked, result.Error.Type);
        Assert.Empty(_tokens.Items);
    }

    [Fact]
    public async Task Ip_outside_allowed_ranges_is_forbidden()
    {
        _users.AllowedIps.Add("192.168.0.0/16");

        var result = await Handler().Handle(new LoginCommand(Email, Password, Ip.Office), CancellationToken.None);

        Assert.Equal("auth.ip_not_allowed", result.Error!.Code);
        Assert.Empty(_tokens.Items);
    }

    [Fact]
    public async Task Ip_inside_allowed_range_is_accepted_including_ipv4_mapped_ipv6()
    {
        _users.AllowedIps.Add("10.1.0.0/16");

        var plain = await Handler().Handle(new LoginCommand(Email, Password, Ip.Office), CancellationToken.None);
        var mapped = await Handler().Handle(new LoginCommand(Email, Password, Ip.Office.MapToIPv6()), CancellationToken.None);

        Assert.True(plain.IsSuccess);
        Assert.True(mapped.IsSuccess);
    }

    [Fact]
    public async Task Unknown_client_ip_is_rejected_when_restriction_exists()
    {
        _users.AllowedIps.Add("10.0.0.0/8");

        var result = await Handler().Handle(new LoginCommand(Email, Password, (IPAddress?)null), CancellationToken.None);

        Assert.Equal("auth.ip_not_allowed", result.Error!.Code);
    }

    [Fact]
    public async Task Two_factor_user_gets_challenge_and_no_tokens()
    {
        _user.EnableTwoFactor();

        var result = await Handler().Handle(new LoginCommand(Email, Password, Ip.Office), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value.Tokens);
        Assert.NotNull(result.Value.TwoFactorChallengeId);
        Assert.Empty(_tokens.Items);
    }

    [Fact]
    public async Task Disabled_account_is_forbidden()
    {
        _user.Disable();

        var result = await Handler().Handle(new LoginCommand(Email, Password, Ip.Office), CancellationToken.None);

        Assert.Equal(ErrorType.Forbidden, result.Error!.Type);
    }
}

public class LoginValidatorTests
{
    [Theory]
    [InlineData("", "x", false)]
    [InlineData("not-an-email", "x", false)]
    [InlineData("a@b.az", "", false)]
    [InlineData("a@b.az", "x", true)]
    public void Validates_input(string email, string password, bool valid)
    {
        var result = new LoginCommandValidator().Validate(new LoginCommand(email, password, null));

        Assert.Equal(valid, result.IsValid);
    }
}
