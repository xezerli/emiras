using System.Collections.Concurrent;
using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.TestSupport;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Notify.IntegrationTests;

public sealed class NotifyFactAttribute : FactAttribute
{
    public NotifyFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresFixture.EnvVar))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(NotifyFixture.AmqpVar)))
        {
            Skip = $"Set {PostgresFixture.EnvVar} and {NotifyFixture.AmqpVar} to run the Notify integration tests.";
        }
    }
}

[CollectionDefinition(Name)]
public sealed class NotifyDefinition : ICollectionFixture<NotifyFixture>
{
    public const string Name = "notify";
}

/// <summary>Real PostgreSQL (tenant kataloqu) + real RabbitMQ + real SignalR protokolu. Hər işə salma unikal exchange prefiksi alır.</summary>
public sealed class NotifyFixture : IAsyncLifetime, IDisposable
{
    public const string AmqpVar = "DENTACORE_TEST_AMQP";

    private readonly TestKeys _keys = new();

    public PostgresFixture Db { get; } = new();

    public string Run { get; } = "nt" + Guid.NewGuid().ToString("N")[..8];

    public string AmqpUri { get; private set; } = string.Empty;

    public string Exchange => $"{Run}.events";

    public async Task InitializeAsync()
    {
        AmqpUri = Environment.GetEnvironmentVariable(AmqpVar) ?? string.Empty;
        await Db.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        if (!string.IsNullOrEmpty(AmqpUri))
        {
            var factory = new RabbitMQ.Client.ConnectionFactory { Uri = new Uri(AmqpUri) };
            await using var connection = await factory.CreateConnectionAsync();
            await using var channel = await connection.CreateChannelAsync();
            await channel.ExchangeDeleteAsync(Exchange);
            await channel.ExchangeDeleteAsync($"{Run}.dead");
        }
    }

    public void Dispose() => _keys.Dispose();

    public ApiFactory<Program> StartHost() => new(
        Db,
        ("Jwt__PublicKeyPem", _keys.PublicPem),
        ("Jwt__AccessTokenQueryPaths__0", "/hubs"),
        ("RabbitMq__Uri", AmqpUri),
        ("RabbitMq__Exchange", Exchange),
        ("RabbitMq__DeadLetterExchange", $"{Run}.dead"),
        ("RabbitMq__QueuePrefix", Run));

    public string Token(string[] permissions, Guid? userId = null, Guid[]? branches = null, bool otherTenant = false, TimeSpan? lifetime = null) =>
        _keys.Mint(userId ?? Guid.NewGuid(), otherTenant ? Db.OtherTenantId : Db.DemoTenantId, permissions, branches?.Select(b => b.ToString()), lifetime: lifetime);

    public IntegrationEnvelope Envelope(string routingKey, object payload, bool otherTenant = false, string? slug = null)
    {
        var json = JsonSerializer.SerializeToElement(payload, IntegrationEnvelope.Json);
        return new IntegrationEnvelope(
            Guid.NewGuid(), "TestEvent", 1, otherTenant ? Db.OtherTenantId : Db.DemoTenantId, slug ?? (otherTenant ? "other" : "demo"), DateTimeOffset.UtcNow, routingKey, null, json);
    }

    /// <summary>WebSocket ilə qoşulur və token <c>?access_token=</c> ilə gedir (brauzer yolu). Qəbul olunanlar <see cref="Client.Received"/>-dadır.</summary>
    public static async Task<Client> ConnectAsync(ApiFactory<Program> host, string token, bool otherTenant = false, bool longPolling = false)
    {
        var server = host.Server;
        var slug = otherTenant ? "other" : "demo";
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, "/hubs/notifications"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.Headers["X-Tenant"] = slug;
                if (longPolling)
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                }
                else
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = false;
                    options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                    options.WebSocketFactory = async (context, cancellationToken) =>
                    {
                        var ws = server.CreateWebSocketClient();
                        ws.ConfigureRequest = request => request.Headers["X-Tenant"] = slug;
                        // Brauzerdəki kimi: WebSocket əl sıxmasında başlıq yoxdur, token query string-dədir
                        var uri = new UriBuilder(context.Uri) { Query = $"{context.Uri.Query.TrimStart('?')}&access_token={Uri.EscapeDataString(token)}".TrimStart('&') }.Uri;
                        return await ws.ConnectAsync(uri, cancellationToken);
                    };
                }
            })
            .Build();
        var client = new Client(connection);
        await connection.StartAsync();
        return client;
    }

    public sealed class Client(HubConnection connection) : IAsyncDisposable
    {
        public HubConnection Connection { get; } = connection;

        public ConcurrentQueue<JsonElement> Received { get; } = new();

        public Task<bool> Closed => _closed.Task;

        private readonly TaskCompletionSource<bool> _closed = new();

        public void Listen()
        {
            Connection.On<JsonElement>("notification", message => Received.Enqueue(message.Clone()));
            Connection.Closed += _ =>
            {
                _closed.TrySetResult(true);
                return Task.CompletedTask;
            };
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    public static async Task<bool> WaitAsync(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
