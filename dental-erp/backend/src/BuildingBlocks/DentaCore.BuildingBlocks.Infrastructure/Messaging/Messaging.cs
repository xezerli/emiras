using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace DentaCore.BuildingBlocks.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    /// <summary>amqp://user:pass@host:5672/vhost. Parol konfiqurasiyadan (mühit dəyişəni/Vault) gəlməlidir.</summary>
    public string Uri { get; set; } = "amqp://guest:guest@localhost:5672/";

    public string Exchange { get; set; } = "dental.events";

    public string DeadLetterExchange { get; set; } = "dental.dead";

    /// <summary>Növbə adlarının prefiksi: dental.&lt;consumer&gt;. Testlər və mühitlər üçün izolyasiya.</summary>
    public string QueuePrefix { get; set; } = "dental";

    public ushort Prefetch { get; set; } = 10;

    public int OutboxBatchSize { get; set; } = 50;

    public int OutboxMaxAttempts { get; set; } = 10;

    public TimeSpan OutboxPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Consumer xətasında yerli təkrar cəhdlər arasındakı gecikmələr; hamısı uğursuz olarsa mesaj DLQ-ya düşür.</summary>
    public TimeSpan[] ConsumerRetryDelays { get; set; } = [TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];
}

/// <summary>
/// Mesaj zərfi (Mərhələ 3 §5). Tenant-ı zərf daşıyır: routing key tenant-sızdır ki, bir növbə bütün tenant-lara xidmət etsin.
/// Payload domen hadisəsinin JSON-udur; yeni sahə əlavə etmək geriyə uyğundur.
/// </summary>
public sealed record IntegrationEnvelope(
    Guid Id,
    string Type,
    int Version,
    Guid TenantId,
    string TenantSlug,
    DateTimeOffset OccurredAt,
    string RoutingKey,
    string? CorrelationId,
    JsonElement Payload)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public T Deserialize<T>() =>
        Payload.Deserialize<T>(Json) ?? throw new JsonException($"Payload of {Type} is empty.");
}

public interface IEventPublisher
{
    /// <summary>Broker mesajı təsdiqləyənə (publisher confirm) qədər gözləyir. Uğursuz olarsa istisna atır, outbox sətri qalır və təkrar cəhd olunur.</summary>
    Task PublishAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken);
}

/// <summary>Consumer-i tanıdır: növbə adı və bağlandığı routing key-lər. Hər consumer öz növbəsinə malikdir (bir modul = bir və ya bir neçə növbə).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ConsumesAttribute(string name, params string[] routingKeys) : Attribute
{
    /// <summary>Növbə: &lt;prefix&gt;.&lt;Name&gt;, məs. dental.patient.no-show.</summary>
    public string Name { get; } = name;

    public string[] RoutingKeys { get; } = routingKeys;
}

public interface IIntegrationConsumer
{
    /// <summary>
    /// Mesajı işləyir. Scope tenant-ın kontekstindədir. At-least-once çatdırılma olduğu üçün təsir
    /// <see cref="InboxExtensions.ExecuteOnceAsync"/> ilə bir dəfəlik olmalıdır.
    /// </summary>
    Task HandleAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken);
}

internal sealed record ConsumerRegistration(Type ConsumerType, string Name, string[] RoutingKeys);

internal static class RabbitMqConfig
{
    public static RabbitMqOptions Read(IConfiguration configuration)
    {
        var options = new RabbitMqOptions();
        configuration.GetSection(RabbitMqOptions.Section).Bind(options);
        return options;
    }
}
