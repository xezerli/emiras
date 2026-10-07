using System.Text;
using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.Scheduling.Contracts;
using DentaCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace DentaCore.Workers.IntegrationTests;

/// <summary>Workers host-u real PostgreSQL və RabbitMQ ilə tam qalxır: outbox → broker → consumer → verilənlər bazası.</summary>
[Collection(MessagingDefinition.Name)]
public sealed class BrokerFlowTests(MessagingFixture fx)
{
    private ApiFactory<Program> StartHost() => new(
        fx.Db,
        ("RabbitMq__Uri", fx.AmqpUri),
        ("RabbitMq__Exchange", fx.Exchange),
        ("RabbitMq__DeadLetterExchange", fx.DeadExchange),
        ("RabbitMq__QueuePrefix", fx.QueuePrefix),
        ("RabbitMq__OutboxPollInterval", "00:00:00.100"),
        ("RabbitMq__ConsumerRetryDelays__0", "00:00:00.050"),
        ("RabbitMq__ConsumerRetryDelays__1", "00:00:00.050"));

    private static object Missed(Guid patientId) => new
    {
        eventId = Guid.NewGuid(),
        occurredAt = DateTimeOffset.UtcNow,
        appointmentId = Guid.NewGuid(),
        patientId,
        providerId = Guid.NewGuid(),
    };

    [MessagingFact]
    public async Task Outbox_row_reaches_the_broker_with_a_complete_envelope()
    {
        await TestData.ResetAsync(fx.Db);
        await using var channel = await TestData.OpenChannelAsync(fx);
        var probe = $"{fx.QueuePrefix}.probe";
        await TestData.BindProbeAsync(channel, fx, probe, "probe.envelope");

        using var host = StartHost();
        using var client = host.CreateClient();
        var id = await TestData.AddOutboxAsync(fx.Db, "probe.envelope", new { hello = "world" });

        var got = await TestData.GetAsync(channel, probe);
        Assert.NotNull(got);
        var envelope = JsonSerializer.Deserialize<IntegrationEnvelope>(got.Value.Body, IntegrationEnvelope.Json)!;
        Assert.Equal(id, envelope.Id);
        Assert.Equal("TestEvent", envelope.Type);
        Assert.Equal("demo", envelope.TenantSlug);
        Assert.Equal(fx.DemoTenantId, envelope.TenantId);
        Assert.Equal("probe.envelope", envelope.RoutingKey);
        Assert.Equal("world", envelope.Payload.GetProperty("hello").GetString());
        Assert.Equal(id.ToString(), got.Value.Result.BasicProperties.MessageId);
        Assert.True(got.Value.Result.BasicProperties.Persistent);

        Assert.True(await MessagingFixture.WaitAsync(async () =>
            await fx.Db.ScalarAsync<DateTime?>(TestData.Schema, "SELECT processed_at FROM outbox_messages WHERE id = $1", id) is not null));
    }

    [MessagingFact]
    public async Task Appointment_missed_increments_no_show_count_exactly_once_even_when_delivered_twice()
    {
        await TestData.ResetAsync(fx.Db);
        var patient = await TestData.AddPatientAsync(fx.Db);
        using var host = StartHost();
        using var client = host.CreateClient();

        var id = await TestData.AddOutboxAsync(fx.Db, "scheduling.appointment-missed", Missed(patient));
        Assert.True(await MessagingFixture.WaitAsync(async () =>
            await fx.Db.ScalarAsync<int>(TestData.Schema, "SELECT no_show_count FROM patients WHERE id = $1", patient) == 1));

        // Eyni mesaj broker-də təkrar çatdırılır (at-least-once): say artmamalıdır
        var publisher = host.Services.GetRequiredService<IEventPublisher>();
        var duplicate = TestData.Envelope(fx.DemoTenantId, "scheduling.appointment-missed", Missed(patient), id);
        await publisher.PublishAsync(duplicate, default);
        await publisher.PublishAsync(duplicate, default);

        // Fərqli mesaj isə sayılır, deməli consumer hələ də canlıdır
        await publisher.PublishAsync(TestData.Envelope(fx.DemoTenantId, "scheduling.appointment-missed", Missed(patient)), default);
        Assert.True(await MessagingFixture.WaitAsync(async () =>
            await fx.Db.ScalarAsync<int>(TestData.Schema, "SELECT no_show_count FROM patients WHERE id = $1", patient) == 2));
        await Task.Delay(500);
        Assert.Equal(2, await fx.Db.ScalarAsync<int>(TestData.Schema, "SELECT no_show_count FROM patients WHERE id = $1", patient));
        Assert.Equal(2, await fx.Db.ScalarAsync<int>(TestData.Schema, "SELECT count(*)::int FROM inbox_messages WHERE consumer = 'patient.no-show'"));
    }

    [MessagingFact]
    public async Task Unreadable_and_permanently_failing_messages_end_up_in_the_dead_letter_queue()
    {
        await TestData.ResetAsync(fx.Db);
        using var host = StartHost();
        using var client = host.CreateClient();
        await using var channel = await TestData.OpenChannelAsync(fx);
        var dlq = $"{fx.QueuePrefix}.patient.no-show.dlq";

        // 1) JSON deyil
        await channel.BasicPublishAsync(fx.Exchange, "scheduling.appointment-missed", false,
            new BasicProperties { Persistent = true }, Encoding.UTF8.GetBytes("this is not json"));
        // 2) Tenant tanınmır: hər cəhd uğursuz olur
        var ghost = TestData.Envelope(Guid.NewGuid(), "scheduling.appointment-missed", Missed(Guid.NewGuid()), tenantSlug: "no-such-tenant");
        await host.Services.GetRequiredService<IEventPublisher>().PublishAsync(ghost, default);

        var first = await TestData.GetAsync(channel, dlq);
        var second = await TestData.GetAsync(channel, dlq);
        Assert.NotNull(first);
        Assert.NotNull(second);
        var bodies = new[] { first.Value.Body, second.Value.Body };
        Assert.Contains("this is not json", bodies);
        Assert.Contains(bodies, b => b.Contains(ghost.Id.ToString(), StringComparison.Ordinal));
    }

    [MessagingFact]
    public async Task Scheduling_domain_event_json_is_readable_through_the_published_contract()
    {
        // Domen hadisəsinin real JSON-u müqavilə tipinə uyğun olmalıdır; sahə adı dəyişsə bu test qırılır.
        var domainEvent = new Scheduling.Domain.AppointmentMissed(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var json = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), IntegrationEnvelope.Json);
        var contract = JsonSerializer.Deserialize<AppointmentMissedV1>(json, IntegrationEnvelope.Json)!;
        Assert.Equal(domainEvent.AppointmentId, contract.AppointmentId);
        Assert.Equal(domainEvent.PatientId, contract.PatientId);
        Assert.Equal(domainEvent.ProviderId, contract.ProviderId);
        Assert.Equal(domainEvent.EventId, contract.EventId);
        await Task.CompletedTask;
    }
}
