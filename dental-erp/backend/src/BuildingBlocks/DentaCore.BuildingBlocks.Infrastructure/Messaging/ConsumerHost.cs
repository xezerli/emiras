using System.Text;
using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DentaCore.BuildingBlocks.Infrastructure.Messaging;

/// <summary>
/// Qeydiyyatdan keçmiş consumer-ləri RabbitMQ növbələrinə bağlayır. Topologiya (exchange, növbə, DLQ) başlanğıcda elan olunur;
/// publisher-dən ƏVVƏL qalxmalıdır ki, subscriber-i olmayan routing key-də mesaj itməsin.
/// Xəta olarsa yerli təkrar cəhdlər (exponential), hamısı uğursuz olarsa mesaj DLQ-ya düşür (itmir).
/// </summary>
public sealed partial class RabbitMqConsumerHost(
    IServiceScopeFactory scopes,
    IEnumerable<ConsumerRegistrationHolder> registrations,
    IOptions<RabbitMqOptions> options,
    ILogger<RabbitMqConsumerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var list = registrations.Select(r => r.Registration).ToList();
        if (list.Count == 0)
        {
            return;
        }

        var o = options.Value;
        var factory = new ConnectionFactory { Uri = new Uri(o.Uri), ClientProvidedName = "dentacore-consumers" };
        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        foreach (var registration in list)
        {
            var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await DeclareAsync(channel, o, registration, stoppingToken);
            await channel.BasicQosAsync(0, o.Prefetch, false, stoppingToken);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, args) => OnMessageAsync(channel, registration, args, o, stoppingToken);
            var queueName = QueueName(o, registration);
            await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, stoppingToken);
            LogStarted(logger, queueName);
        }

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal dayanma
        }
    }

    internal static string QueueName(RabbitMqOptions o, ConsumerRegistration r) => $"{o.QueuePrefix}.{r.Name}";

    internal static async Task DeclareAsync(IChannel channel, RabbitMqOptions o, ConsumerRegistration r, CancellationToken cancellationToken)
    {
        var queue = QueueName(o, r);
        await channel.ExchangeDeclareAsync(o.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(o.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync($"{queue}.dlq", durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync($"{queue}.dlq", o.DeadLetterExchange, queue, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-dead-letter-exchange"] = o.DeadLetterExchange, ["x-dead-letter-routing-key"] = queue },
            cancellationToken: cancellationToken);
        foreach (var key in r.RoutingKeys)
        {
            await channel.QueueBindAsync(queue, o.Exchange, key, cancellationToken: cancellationToken);
        }
    }

    private async Task OnMessageAsync(IChannel channel, ConsumerRegistration registration, BasicDeliverEventArgs args, RabbitMqOptions o, CancellationToken ct)
    {
        IntegrationEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<IntegrationEnvelope>(Encoding.UTF8.GetString(args.Body.Span), IntegrationEnvelope.Json);
        }
        catch (JsonException)
        {
            envelope = null;
        }

        if (envelope is null)
        {
            // Oxuna bilməyən mesaj heç vaxt uğurlu olmayacaq: təkrar cəhd etmədən DLQ-ya
            LogPoison(logger, registration.Name);
            await channel.BasicNackAsync(args.DeliveryTag, false, requeue: false, ct);
            return;
        }

        var delays = o.ConsumerRetryDelays;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await HandleOnceAsync(registration, envelope, ct);
                await channel.BasicAckAsync(args.DeliveryTag, false, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                // Dayanma: ack olunmur, broker mesajı yenidən çatdıracaq
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= delays.Length)
                {
                    LogDeadLettered(logger, registration.Name, envelope.Id, ex);
                    await channel.BasicNackAsync(args.DeliveryTag, false, requeue: false, ct);
                    return;
                }

                LogRetry(logger, registration.Name, envelope.Id, attempt + 1, ex);
                await Task.Delay(delays[attempt], ct);
            }
        }
    }

    private async Task HandleOnceAsync(ConsumerRegistration registration, IntegrationEnvelope envelope, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
        var tenant = await directory.FindBySlugAsync(envelope.TenantSlug, ct)
            ?? throw new InvalidOperationException($"Tenant '{envelope.TenantSlug}' is unknown or inactive.");
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
        var consumer = (IIntegrationConsumer)scope.ServiceProvider.GetRequiredService(registration.ConsumerType);
        await consumer.HandleAsync(envelope, ct);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Consumer listening on {Queue}")]
    private static partial void LogStarted(ILogger logger, string queue);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unreadable message on {Consumer} sent to the dead-letter queue")]
    private static partial void LogPoison(ILogger logger, string consumer);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer {Consumer} failed on {MessageId} (attempt {Attempt}); retrying")]
    private static partial void LogRetry(ILogger logger, string consumer, Guid messageId, int attempt, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Consumer {Consumer} gave up on {MessageId}; dead-lettered")]
    private static partial void LogDeadLettered(ILogger logger, string consumer, Guid messageId, Exception exception);
}

/// <summary>DI-da IEnumerable&lt;ConsumerRegistration&gt; (internal tip) açıqlamamaq üçün sarğı.</summary>
public sealed class ConsumerRegistrationHolder
{
    internal ConsumerRegistrationHolder(ConsumerRegistration registration) => Registration = registration;

    internal ConsumerRegistration Registration { get; }
}
