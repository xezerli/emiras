using DentaCore.Identity.Application;
using DentaCore.Identity.Domain;

namespace DentaCore.Identity.UnitTests;

public class AdminUnitTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    private static User NewUser() => User.Register(Guid.NewGuid(), [1], [2], "hash", "Dr. Test");

    [Theory]
    [InlineData("Str0ng-Passw0rd", true)]
    [InlineData("short1", false)]
    [InlineData("onlyletters-here", false)]
    [InlineData("12345678901234", false)]
    [InlineData("", false)]
    public void Password_policy_requires_length_a_letter_and_a_digit(string password, bool valid) =>
        Assert.Equal(valid, new ResetPasswordCommandValidator().Validate(new ResetPasswordCommand(Guid.NewGuid(), password)).IsValid);

    [Fact]
    public void Enable_reopens_a_disabled_and_locked_account()
    {
        var user = NewUser();
        for (var i = 0; i < 5; i++)
        {
            user.RegisterFailedLogin(Now, LockoutPolicy.Default);
        }

        user.Disable();
        Assert.True(user.CanAttemptSignIn(Now).IsFailure);

        user.Enable();

        Assert.True(user.CanAttemptSignIn(Now).IsSuccess);
        Assert.Equal(UserStatus.Active, user.Status);
        Assert.Equal(0, user.FailedLogins);
    }

    [Fact]
    public void ChangePassword_replaces_the_hash_and_clears_the_lockout()
    {
        var user = NewUser();
        for (var i = 0; i < 5; i++)
        {
            user.RegisterFailedLogin(Now, LockoutPolicy.Default);
        }

        user.ChangePassword("new-hash");

        Assert.Equal("new-hash", user.PasswordHash);
        Assert.False(user.IsLockedAt(Now));
    }

    [Fact]
    public void Rename_trims_and_rejects_blank_names()
    {
        var user = NewUser();

        user.Rename("  Leyla Məmmədova ");

        Assert.Equal("Leyla Məmmədova", user.FullName);
        Assert.ThrowsAny<ArgumentException>(() => user.Rename("  "));
    }

    [Fact]
    public void Role_permission_validator_rejects_duplicates_bad_scopes_and_negative_limits()
    {
        var validator = new SetRolePermissionsCommandValidator();

        Assert.True(validator.Validate(new SetRolePermissionsCommand("doctor", [new RoleGrant("patient:read", "own", null)])).IsValid);
        Assert.False(validator.Validate(new SetRolePermissionsCommand("doctor", [new RoleGrant("patient:read", "own", null), new RoleGrant("patient:read", "branch", null)])).IsValid);
        Assert.False(validator.Validate(new SetRolePermissionsCommand("doctor", [new RoleGrant("patient:read", "everywhere", null)])).IsValid);
        Assert.False(validator.Validate(new SetRolePermissionsCommand("doctor", [new RoleGrant("invoice:refund", "branch", -1m)])).IsValid);
    }

    [Fact]
    public void Create_user_validator_requires_one_to_ten_distinct_roles()
    {
        var validator = new CreateUserCommandValidator();
        CreateUserCommand With(params RoleAssignment[] roles) => new("a@b.az", "Ad Soyad", "Str0ng-Passw0rd", roles);

        Assert.True(validator.Validate(With(new RoleAssignment("doctor", null))).IsValid);
        Assert.False(validator.Validate(With()).IsValid);
        Assert.False(validator.Validate(With(new RoleAssignment("doctor", null), new RoleAssignment("doctor", Guid.NewGuid()))).IsValid);
        Assert.False(validator.Validate(With(new RoleAssignment(" ", null))).IsValid);
    }
}
