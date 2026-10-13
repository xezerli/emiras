using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DentaCore.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ publisher. Bir bağlantı və bir kanal (publisher confirms açıq), paralel çağırışlar semafor ilə ardıcıllaşır.
/// Bağlantı kəsilərsə sonrakı çağırış yenidən qoşulur; mesaj təsdiqlənməyibsə istisna atılır (outbox sətri qalır).
/// </summary>
public sealed partial class RabbitMqPublisher(IOptions<RabbitMqOptions> options, ILogger<RabbitMqPublisher> logger) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var o = options.Value;
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, IntegrationEnvelope.Json));
        var properties = new BasicProperties
        {
            Persistent = true,
            MessageId = envelope.Id.ToString(),
            Type = envelope.Type,
            ContentType = "application/json",
            Timestamp = new AmqpTimestamp(envelope.OccurredAt.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?> { ["tenant"] = envelope.TenantSlug, ["version"] = envelope.Version },
        };

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await EnsureChannelAsync(o, cancellationToken);
            // mandatory=false: subscriber-i olmayan hadisə (məs. audit) xəta deyil. Növbələri consumer host-lar publisher-dən əvvəl elan edir.
            await channel.BasicPublishAsync(o.Exchange, envelope.RoutingKey, mandatory: false, basicProperties: properties, body: body, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await ResetAsync();
            LogPublishFailed(logger, envelope.Type, envelope.Id, ex);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }

    private async Task<IChannel> EnsureChannelAsync(RabbitMqOptions o, CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await ResetAsync();
        var factory = new ConnectionFactory { Uri = new Uri(o.Uri), ClientProvidedName = "dentacore-publisher" };
        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken);
        await _channel.ExchangeDeclareAsync(o.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        return _channel;
    }

    private async Task ResetAsync()
    {
        try
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Bağlantı artıq ölüdür, bağlamaq xətası əhəmiyyətsizdir
        }
        finally
        {
            _channel = null;
            _connection = null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing {Type} ({MessageId}) failed")]
    private static partial void LogPublishFailed(ILogger logger, string type, Guid messageId, Exception exception);
}
