using DentaCore.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace DentaCore.Workers.IntegrationTests;

/// <summary>Testlər yalnız həm DENTACORE_TEST_PG, həm də DENTACORE_TEST_AMQP təyin olunubsa işləyir.</summary>
public sealed class MessagingFactAttribute : FactAttribute
{
    public MessagingFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFixture.EnvVar))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MessagingFixture.EnvVar)))
        {
            Skip = $"Set {PostgresFixture.EnvVar} and {MessagingFixture.EnvVar} (amqp://...) to run messaging integration tests.";
        }
    }
}

/// <summary>
/// Real PostgreSQL + real RabbitMQ. Hər işə salma unikal exchange/növbə prefiksi alır: paralel işə salmalar və köhnə qalıqlar bir-birinə toxunmur.
/// </summary>
public sealed class MessagingFixture : IAsyncLifetime
{
    public const string EnvVar = "DENTACORE_TEST_AMQP";

    public PostgresFixture Db { get; } = new();

    public string Run { get; } = "it" + Guid.NewGuid().ToString("N")[..8];

    public string AmqpUri { get; private set; } = string.Empty;

    public string Exchange => $"{Run}.events";

    public string DeadExchange => $"{Run}.dead";

    public string QueuePrefix => Run;

    public Guid DemoTenantId => Db.DemoTenantId;

    public async Task InitializeAsync()
    {
        AmqpUri = Environment.GetEnvironmentVariable(EnvVar) ?? string.Empty;
        await Db.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        if (string.IsNullOrEmpty(AmqpUri))
        {
            return;
        }

        var factory = new ConnectionFactory { Uri = new Uri(AmqpUri) };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        foreach (var queue in new[] { "patient.no-show", "billing.invoice-draft", "probe" })
        {
            foreach (var name in new[] { $"{QueuePrefix}.{queue}", $"{QueuePrefix}.{queue}.dlq" })
            {
                await channel.QueueDeleteAsync(name);
            }
        }

        await channel.ExchangeDeleteAsync(Exchange);
        await channel.ExchangeDeleteAsync(DeadExchange);
    }

    public Dictionary<string, string?> Settings(params (string Key, string Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = Db.ConnectionString,
            ["Tenancy:AllowTenantHeader"] = "true",
            ["Security:PiiEncryptionKey"] = Db.EncryptionKey,
            ["Security:PiiHashKey"] = Db.HashKey,
            ["RabbitMq:Uri"] = AmqpUri,
            ["RabbitMq:Exchange"] = Exchange,
            ["RabbitMq:DeadLetterExchange"] = DeadExchange,
            ["RabbitMq:QueuePrefix"] = QueuePrefix,
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    /// <summary>Hosted servislər olmadan: yalnız publisher/processor/maintenance. Testlər öz IEventPublisher-ini qoşa bilər.</summary>
    public ServiceProvider BuildProcessorServices(Action<IServiceCollection>? configure = null, params (string Key, string Value)[] extra)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(Settings(extra)).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IConfiguration>(configuration);
        DentaCore.BuildingBlocks.Infrastructure.Messaging.MessagingServiceCollectionExtensions.AddMessaging(services, configuration);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public static async Task<bool> WaitAsync(Func<Task<bool>> condition, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return await condition();
    }
}

[CollectionDefinition(Name)]
public sealed class MessagingDefinition : ICollectionFixture<MessagingFixture>
{
    public const string Name = "messaging";
}
