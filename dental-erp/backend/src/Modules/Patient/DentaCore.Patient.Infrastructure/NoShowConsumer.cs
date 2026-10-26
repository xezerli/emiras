using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.Patient.Infrastructure.Persistence;
using DentaCore.Scheduling.Contracts;
using Microsoft.EntityFrameworkCore;

namespace DentaCore.Patient.Infrastructure;

/// <summary>
/// Gəlmədi hadisəsi pasiyentin no_show_count-unu artırır. Artırma atomik SQL-dir (oxu-dəyiş-yaz yox),
/// ona görə paralel redaktə ilə toqquşmur; inbox ilə eyni mesaj ikinci dəfə say artırmır.
/// </summary>
[Consumes("patient.no-show", "scheduling.appointment-missed")]
public sealed class NoShowConsumer(PatientDbContext db) : IIntegrationConsumer
{
    public async Task HandleAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var message = envelope.Deserialize<AppointmentMissedV1>();
        await db.ExecuteOnceAsync(envelope.Id, "patient.no-show", async ct =>
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE patients SET no_show_count = no_show_count + 1 WHERE id = {message.PatientId} AND deleted_at IS NULL",
                ct);
        }, cancellationToken);
    }
}
