using System.Collections.Concurrent;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Workers.IntegrationTests;

[Collection(MessagingDefinition.Name)]
public sealed class OutboxProcessorTests(MessagingFixture fx)
{
    private sealed class RecordingPublisher(TimeSpan? delay = null, bool fail = false) : IEventPublisher
    {
        public ConcurrentBag<Guid> Sent { get; } = [];

        public async Task PublishAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken)
        {
            if (delay is { } d)
            {
                await Task.Delay(d, cancellationToken);
            }

            if (fail)
            {
                throw new InvalidOperationException("broker is down");
            }

            Sent.Add(envelope.Id);
        }
    }

    [MessagingFact]
    public async Task Failed_publish_records_attempt_and_backs_off_then_recovers()
    {
        await TestData.ResetAsync(fx.Db);
        var id = await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 1 });

        await using (var failing = fx.BuildProcessorServices(s => s.AddSingleton<IEventPublisher>(new RecordingPublisher(fail: true))))
        {
            var sent = await failing.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(default);
            Assert.Equal(0, sent);
        }

        Assert.Equal(1, await fx.Db.ScalarAsync<int>(TestData.Schema, "SELECT attempts FROM outbox_messages WHERE id = $1", id));
        Assert.Contains("broker is down", await fx.Db.ScalarAsync<string>(TestData.Schema, "SELECT last_error FROM outbox_messages WHERE id = $1", id));
        Assert.True(await fx.Db.ScalarAsync<bool>(TestData.Schema, "SELECT next_attempt_at > now() + interval '3 seconds' FROM outbox_messages WHERE id = $1", id));

        // backoff müddətində yeni cəhd edilmir
        var ok = new RecordingPublisher();
        await using var healthy = fx.BuildProcessorServices(s => s.AddSingleton<IEventPublisher>(ok));
        Assert.Equal(0, await healthy.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(default));
        Assert.Empty(ok.Sent);

        // vaxt çatdıqda göndərilir və xəta təmizlənir
        await fx.Db.ExecAsync(TestData.Schema, "UPDATE outbox_messages SET next_attempt_at = now() WHERE id = $1", id);
        Assert.Equal(1, await healthy.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(default));
        Assert.Equal([id], ok.Sent.ToArray());
        Assert.Null(await fx.Db.ScalarAsync<string>(TestData.Schema, "SELECT last_error FROM outbox_messages WHERE id = $1", id));
        Assert.NotNull(await fx.Db.ScalarAsync<DateTime?>(TestData.Schema, "SELECT processed_at FROM outbox_messages WHERE id = $1", id));
    }

    [MessagingFact]
    public async Task Message_beyond_max_attempts_is_left_alone()
    {
        await TestData.ResetAsync(fx.Db);
        var id = await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 1 });
        await fx.Db.ExecAsync(TestData.Schema, "UPDATE outbox_messages SET attempts = 10 WHERE id = $1", id);

        var ok = new RecordingPublisher();
        await using var sp = fx.BuildProcessorServices(s => s.AddSingleton<IEventPublisher>(ok));
        Assert.Equal(0, await sp.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(default));
        Assert.Empty(ok.Sent);
    }

    [MessagingFact]
    public async Task Concurrent_processors_never_publish_the_same_message_twice()
    {
        await TestData.ResetAsync(fx.Db);
        var ids = new List<Guid>();
        for (var i = 0; i < 30; i++)
        {
            ids.Add(await TestData.AddOutboxAsync(fx.Db, "probe.x", new { i }));
        }

        var publisher = new RecordingPublisher(delay: TimeSpan.FromMilliseconds(20));
        void Configure(IServiceCollection s) => s.AddSingleton<IEventPublisher>(publisher);
        await using var a = fx.BuildProcessorServices(Configure, ("RabbitMq:OutboxBatchSize", "5"));
        await using var b = fx.BuildProcessorServices(Configure, ("RabbitMq:OutboxBatchSize", "5"));
        var pa = a.GetRequiredService<OutboxProcessor>();
        var pb = b.GetRequiredService<OutboxProcessor>();

        async Task<int> Drain(OutboxProcessor p)
        {
            var total = 0;
            int n;
            while ((n = await p.ProcessOnceAsync(default)) > 0)
            {
                total += n;
            }

            return total;
        }

        var results = await Task.WhenAll(Task.Run(() => Drain(pa)), Task.Run(() => Drain(pb)));
        Assert.Equal(30, results.Sum());
        Assert.Equal(30, publisher.Sent.Count);
        Assert.Equal(30, publisher.Sent.Distinct().Count());
        Assert.Equal(ids.Order(), publisher.Sent.Order());
        Assert.True(results.All(r => r > 0), $"both workers should share the load, got {string.Join('/', results)}");
    }

    [MessagingFact]
    public async Task Maintenance_creates_audit_partitions_two_months_ahead_and_is_idempotent()
    {
        const string countSql = "SELECT count(*)::int FROM pg_inherits WHERE inhparent = 'audit_log'::regclass";
        var before = await fx.Db.ScalarAsync<int>(TestData.Schema, countSql);
        await using var sp = fx.BuildProcessorServices();
        var maintenance = sp.GetRequiredService<TenantMaintenance>();
        await maintenance.RunOnceAsync(default);
        var after = await fx.Db.ScalarAsync<int>(TestData.Schema, countSql);
        Assert.True(after >= 3 && after >= before, $"before={before} after={after}");

        var twoAhead = await fx.Db.ScalarAsync<string>(
            TestData.Schema, "SELECT to_char(current_date + interval '2 month', '\"audit_log_\"YYYY_MM')");
        Assert.True(await fx.Db.ScalarAsync<bool>(TestData.Schema, "SELECT to_regclass($1) IS NOT NULL", twoAhead!));

        await maintenance.RunOnceAsync(default);
        Assert.Equal(after, await fx.Db.ScalarAsync<int>(TestData.Schema, countSql));
    }
}
