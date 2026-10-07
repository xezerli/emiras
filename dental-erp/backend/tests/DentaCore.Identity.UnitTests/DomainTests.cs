using DentaCore.Identity.Domain;

namespace DentaCore.Identity.UnitTests;

public class UserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static User NewUser() => User.Register(Guid.NewGuid(), [1], [2], "hash", "Dr. Test");

    [Fact]
    public void Locks_account_after_max_failed_attempts_and_raises_event()
    {
        var user = NewUser();
        for (var i = 0; i < 4; i++)
        {
            user.RegisterFailedLogin(Now, LockoutPolicy.Default);
        }

        Assert.False(user.IsLockedAt(Now));

        user.RegisterFailedLogin(Now, LockoutPolicy.Default);

        Assert.True(user.IsLockedAt(Now));
        Assert.True(user.CanAttemptSignIn(Now).IsFailure);
        Assert.Equal("auth.account_locked", user.CanAttemptSignIn(Now).Error!.Code);
        Assert.Contains(user.DomainEvents, e => e is UserLockedOut);
    }

    [Fact]
    public void Lock_expires_after_duration()
    {
        var user = NewUser();
        for (var i = 0; i < 5; i++)
        {
            user.RegisterFailedLogin(Now, LockoutPolicy.Default);
        }

        var later = Now + LockoutPolicy.Default.LockDuration + TimeSpan.FromSeconds(1);

        Assert.False(user.IsLockedAt(later));
        Assert.True(user.CanAttemptSignIn(later).IsSuccess);
    }

    [Fact]
    public void Successful_login_resets_counter()
    {
        var user = NewUser();
        user.RegisterFailedLogin(Now, LockoutPolicy.Default);
        user.RegisterFailedLogin(Now, LockoutPolicy.Default);

        user.RegisterSuccessfulLogin(Now, null);

        Assert.Equal(0, user.FailedLogins);
        Assert.Equal(Now, user.LastLoginAt);
        Assert.Contains(user.DomainEvents, e => e is UserLoggedIn);
    }

    [Fact]
    public void Disabled_user_cannot_sign_in()
    {
        var user = NewUser();
        user.Disable();

        var result = user.CanAttemptSignIn(Now);

        Assert.Equal("auth.account_disabled", result.Error!.Code);
    }

    [Fact]
    public void Register_requires_name_and_password_hash()
    {
        Assert.Throws<ArgumentException>(() => User.Register(Guid.NewGuid(), [1], [2], "", "x"));
        Assert.Throws<ArgumentException>(() => User.Register(Guid.NewGuid(), [1], [2], "h", " "));
    }
}

public class RefreshTokenTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Issue_sets_expiry_and_is_not_consumed()
    {
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [9], Now, TimeSpan.FromDays(7), null);

        Assert.Equal(Now.AddDays(7), token.ExpiresAt);
        Assert.False(token.IsConsumedOrRevoked);
        Assert.False(token.IsExpired(Now.AddDays(6)));
        Assert.True(token.IsExpired(Now.AddDays(7)));
    }

    [Fact]
    public void Reuse_raises_event_with_family()
    {
        var family = Guid.NewGuid();
        var token = RefreshToken.Issue(Guid.NewGuid(), Guid.NewGuid(), family, [9], Now, TimeSpan.FromDays(7), null);

        token.RaiseReuseDetected(Now, null);

        var e = Assert.IsType<RefreshTokenReuseDetected>(Assert.Single(token.DomainEvents));
        Assert.Equal(family, e.FamilyId);
    }
}
