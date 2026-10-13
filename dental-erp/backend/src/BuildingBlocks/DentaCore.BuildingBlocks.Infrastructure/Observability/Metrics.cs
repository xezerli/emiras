using System.Diagnostics.Metrics;

namespace DentaCore.BuildingBlocks.Infrastructure.Observability;

/// <summary>
/// Biznes səviyyəli metrikalar (<c>System.Diagnostics.Metrics</c>: əlavə asılılıq və scrape endpoint-i yoxdur).
/// Tenant adı etiket DEYİL (kardinallıq partlayışı və tenant məlumatının sızması): bütün tenant-lar üzrə cəmdir.
/// </summary>
public static class DentaCoreMetrics
{
    public const string MeterName = "DentaCore";

    private static readonly Meter Meter = new(MeterName, "1.0");

    private static readonly Counter<long> Published = Meter.CreateCounter<long>("dentacore.outbox.published", "{message}", "Brokerə göndərilmiş outbox mesajları");

    private static readonly Counter<long> PublishFailures = Meter.CreateCounter<long>("dentacore.outbox.publish_failures", "{message}", "Göndərilməsi uğursuz olmuş outbox mesajları");

    private static readonly Histogram<double> PublishDelay = Meter.CreateHistogram<double>("dentacore.outbox.publish_delay", "s", "Hadisənin yaranmasından brokerə çatmasına qədər gecikmə");

    private static OutboxSnapshot _outbox = new(0, 0, 0);

    static DentaCoreMetrics()
    {
        // Sayğaclar ilk artıma qədər ixrac olunmur; "göndərilən sayı 0-dır" qaydası (OutboxPublisherStalled) üçün sıfır nöqtəsi ilk dəqiqədən görünməlidir
        Published.Add(0);
        PublishFailures.Add(0);
        Meter.CreateObservableGauge("dentacore.outbox.pending", () => _outbox.Pending, "{message}", "Göndərilməyi gözləyən outbox mesajları (cəhd limiti aşılmayanlar)");
        Meter.CreateObservableGauge("dentacore.outbox.dead", () => _outbox.Dead, "{message}", "Cəhd limiti aşılmış, əl müdaxiləsi gözləyən outbox mesajları");
        Meter.CreateObservableGauge("dentacore.outbox.oldest_age", () => _outbox.OldestAgeSeconds, "s", "Ən köhnə göndərilməmiş outbox mesajının yaşı");
    }

    public static void RecordPublished(DateTimeOffset occurredAt, DateTimeOffset now)
    {
        Published.Add(1);
        PublishDelay.Record(Math.Max(0, (now - occurredAt).TotalSeconds));
    }

    public static void RecordPublishFailure() => PublishFailures.Add(1);

    /// <summary>Son nümunələmənin nəticəsi (bütün tenant-lar üzrə cəm; yaş üçün maksimum).</summary>
    public static void SetOutboxSnapshot(long pending, long dead, double oldestAgeSeconds) =>
        Volatile.Write(ref _outbox, new OutboxSnapshot(pending, dead, oldestAgeSeconds));

    public static (long Pending, long Dead, double OldestAgeSeconds) CurrentOutbox
    {
        get
        {
            var s = Volatile.Read(ref _outbox);
            return (s.Pending, s.Dead, s.OldestAgeSeconds);
        }
    }

    private sealed record OutboxSnapshot(long Pending, long Dead, double OldestAgeSeconds);
}
