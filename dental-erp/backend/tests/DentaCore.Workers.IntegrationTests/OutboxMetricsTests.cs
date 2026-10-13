using System.Diagnostics.Metrics;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.BuildingBlocks.Infrastructure.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Workers.IntegrationTests;

[Collection(MessagingDefinition.Name)]
public sealed class OutboxMetricsTests(MessagingFixture fx)
{
    private sealed class Publisher(bool fail = false) : IEventPublisher
    {
        public Task PublishAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken) =>
            fail ? throw new InvalidOperationException("broker is down") : Task.CompletedTask;
    }

    /// <summary>Metrik ölçmələrini yığır (MeterListener): real OTel ixracı olmadan, tətbiqin yaydığı dəyərləri yoxlayır.</summary>
    private sealed class Capture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, double> _sums = [];

        public Capture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == DentaCoreMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((i, v, _, _) => Add(i.Name, v));
            _listener.SetMeasurementEventCallback<double>((i, v, _, _) => Add(i.Name, v));
            _listener.Start();
        }

        public double Sum(string name) => _sums.GetValueOrDefault(name);

        public void Poll() => _listener.RecordObservableInstruments();

        public void Dispose() => _listener.Dispose();

        private void Add(string name, double value)
        {
            lock (_sums)
            {
                _sums[name] = _sums.GetValueOrDefault(name) + value;
            }
        }
    }

    [MessagingFact]
    public async Task Published_and_failed_messages_are_counted_with_a_delay_sample()
    {
        await TestData.ResetAsync(fx.Db);
        await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 1 });
        await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 2 });
        using var capture = new Capture();

        await using (var ok = fx.BuildProcessorServices(s => s.AddSingleton<IEventPublisher>(new Publisher())))
        {
            Assert.Equal(2, await ok.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(default));
        }

        Assert.Equal(2, capture.Sum("dentacore.outbox.published"));
        Assert.True(capture.Sum("dentacore.outbox.publish_delay") >= 0);

        await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 3 });
        await using (var down = fx.BuildProcessorServices(s => s.AddSingleton<IEventPublisher>(new Publisher(fail: true))))
        {
            Assert.Equal(0, await down.GetRequiredService<OutboxProcessor>().ProcessOnceAsync(default));
        }

        Assert.Equal(1, capture.Sum("dentacore.outbox.publish_failures"));
        Assert.Equal(2, capture.Sum("dentacore.outbox.published"));   // uğursuz göndərmə "göndərilib" sayılmır
    }

    [MessagingFact]
    public async Task Sampler_separates_pending_from_dead_letters_and_reports_the_oldest_age()
    {
        await TestData.ResetAsync(fx.Db);
        var old = await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 1 });
        await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 2 });
        var dead = await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 3 });
        await TestData.AddOutboxAsync(fx.Db, "probe.x", new { a = 4 });
        await fx.Db.ExecAsync(TestData.Schema, "UPDATE outbox_messages SET occurred_at = now() - interval '90 seconds' WHERE id = $1", old);
        await fx.Db.ExecAsync(TestData.Schema, "UPDATE outbox_messages SET attempts = 10 WHERE id = $1", dead);
        await fx.Db.ExecAsync(TestData.Schema, "UPDATE outbox_messages SET processed_at = now() WHERE payload->>'a' = '4'");   // göndərilmiş: sayılmamalıdır

        await using var sp = fx.BuildProcessorServices(s => s.AddSingleton<IEventPublisher>(new Publisher()));
        await sp.GetRequiredService<OutboxProcessor>().SampleStatsAsync(default);

        var (pending, deadCount, oldest) = DentaCoreMetrics.CurrentOutbox;
        Assert.Equal(2, pending);          // old + a=2
        Assert.Equal(1, deadCount);        // cəhd limiti aşılan
        Assert.InRange(oldest, 89, 120);   // ölü mesajın yaşı "ən köhnə gözləyən"ə qarışmır

        using var capture = new Capture();
        capture.Poll();
        Assert.Equal(2, capture.Sum("dentacore.outbox.pending"));
        Assert.Equal(1, capture.Sum("dentacore.outbox.dead"));
    }
}
