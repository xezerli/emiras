using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Domain;
using DentaCore.Identity.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DentaCore.Identity.UnitTests;

/// <summary>Dispatcher + behavior zənciri real DI ilə: validasiya handler-ə çatmamış kəsir, uğurlu əmrdən sonra UnitOfWork saxlayır.</summary>
public class PipelineTests
{
    private static (ISender Sender, FakeUnitOfWork Uow, FakeUsers Users) Build()
    {
        var uow = new FakeUnitOfWork();
        var users = new FakeUsers();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddApplicationCore(typeof(LoginCommand).Assembly);
        services.AddSingleton<IClock>(new FakeClock(DateTimeOffset.UtcNow));
        services.AddSingleton(users);
        services.AddSingleton<IUserRepository>(users);
        services.AddSingleton(new FakeRefreshTokens());
        services.AddSingleton<IRefreshTokenRepository>(sp => sp.GetRequiredService<FakeRefreshTokens>());
        services.AddSingleton<IPasswordHasher, FakeHasher>();
        services.AddSingleton<IPiiProtector>(TestPii.Create());
        services.AddSingleton<IAccessTokenIssuer, FakeTokenIssuer>();
        services.AddSingleton<IRefreshTokenFactory, DentaCore.Identity.Infrastructure.Security.RefreshTokenFactory>();
        services.AddSingleton<ITenantContext, FakeTenant>();
        services.AddSingleton<ICurrentUser>(CurrentUser.Anonymous);
        services.AddSingleton(uow);
        services.AddSingleton<IIdentityUnitOfWork>(uow);
        services.AddSingleton<IModuleUnitOfWork>(uow);
        return (services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ISender>(), uow, users);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_by_validation_before_handler_runs()
    {
        var (sender, uow, _) = Build();

        var result = await sender.Send(new LoginCommand("not-an-email", "x", null));

        Assert.Equal(ErrorType.Validation, result.Error!.Type);
        Assert.Equal(0, uow.Saves);
    }

    [Fact]
    public async Task Successful_command_saves_via_module_unit_of_work()
    {
        var (sender, uow, users) = Build();
        var pii = TestPii.Create();
        users.Items.Add(DentaCore.Identity.Domain.User.Register(Guid.NewGuid(), pii.BlindIndex("a@b.az"), pii.Encrypt("a@b.az"), "hash:pw", "Dr. A"));

        var result = await sender.Send(new LoginCommand("a@b.az", "pw", null));

        Assert.True(result.IsSuccess);
        Assert.Equal(1, uow.Saves);
    }

    [Fact]
    public async Task Failed_command_does_not_trigger_pipeline_save()
    {
        var (sender, uow, _) = Build();

        var result = await sender.Send(new LoginCommand("nobody@b.az", "pw", null));

        Assert.True(result.IsFailure);
        Assert.Equal(0, uow.Saves);
    }
}

public class ResultTests
{
    [Fact]
    public void Failed_result_cannot_expose_value()
    {
        Result<int> failed = Error.NotFound("x", "nope");

        Assert.Throws<InvalidOperationException>(() => failed.Value);
    }

    [Fact]
    public void Implicit_conversions_work_both_ways()
    {
        Result<int> ok = 5;
        Result<int> fail = Error.Conflict("c", "m");

        Assert.Equal(5, ok.Value);
        Assert.Equal(ErrorType.Conflict, fail.Error!.Type);
    }
}
