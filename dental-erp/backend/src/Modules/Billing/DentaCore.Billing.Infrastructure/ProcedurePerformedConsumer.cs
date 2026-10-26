using DentaCore.Billing.Application;
using DentaCore.Billing.Domain;
using DentaCore.Billing.Infrastructure.Persistence;
using DentaCore.BuildingBlocks.Application;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.Clinical.Contracts;
using DentaCore.Patient.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DentaCore.Billing.Infrastructure;

/// <summary>
/// İcra olunan prosedur vizitin QARALAMA fakturasına sətir kimi əlavə olunur (vizit üçün qaralama yoxdursa yaradılır).
/// Qiymət və endirim icra anındakı plan bəndindən gəlir. Faktura rəsmiləşdirilməsi (issue) insan qərarıdır: reception yoxlayıb təsdiqləyir.
/// Eyni plan bəndi ikinci dəfə fakturalanmır (unikal indeks + əvvəlcədən yoxlama).
/// </summary>
[Consumes("billing.invoice-draft", "clinical.procedure-performed")]
public sealed class ProcedurePerformedConsumer(
    BillingDbContext db, IPatientDirectory patients, IClock clock, IOptions<BillingOptions> options) : IIntegrationConsumer
{
    public async Task HandleAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var message = envelope.Deserialize<ProcedurePerformedV1>();
        await db.ExecuteOnceAsync(envelope.Id, "billing.invoice-draft", ct => ApplyAsync(message, ct), cancellationToken);
    }

    private async Task ApplyAsync(ProcedurePerformedV1 message, CancellationToken cancellationToken)
    {
        if (await db.Database.SqlQuery<bool>($"SELECT EXISTS (SELECT 1 FROM invoice_items WHERE plan_item_id = {message.PlanItemId}) AS \"Value\"").SingleAsync(cancellationToken))
        {
            return;   // artıq fakturalanıb
        }

        var patient = await patients.FindAsync(message.PatientId, cancellationToken)
            ?? throw new InvalidOperationException($"Patient {message.PatientId} not found for procedure {message.PlanItemId}.");
        var info = await db.Database.SqlQuery<ProcedureInfo>(
            $"SELECT COALESCE(s.name, p.name) AS name, s.id AS service_id, s.vat_rate AS vat_rate, s.currency AS currency FROM procedure_codes p LEFT JOIN LATERAL (SELECT id, name, vat_rate, currency FROM services WHERE procedure_code = p.code AND is_active ORDER BY valid_from DESC LIMIT 1) s ON true WHERE p.code = {message.ProcedureCode}")
            .FirstOrDefaultAsync(cancellationToken);

        var gross = message.Quantity * message.UnitPrice;
        var item = new NewInvoiceItem(
            info?.ServiceId, message.PlanItemId, info?.Name ?? message.ProcedureCode, message.ToothFdi, message.ProviderId, message.Quantity, message.UnitPrice,
            Money.Round(gross * message.DiscountPercent / 100m), info?.VatRate ?? 0m);

        var draft = await db.Invoices.Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.VisitId == message.VisitId && i.Kind == InvoiceKind.Invoice && i.Status == InvoiceStatus.Draft, cancellationToken);
        if (draft is not null)
        {
            Unwrap(draft.AddItem(item));
            return;
        }

        var created = Invoice.Create(
            Guid.NewGuid(), InvoiceKind.Invoice, patient.BranchId, message.PatientId, message.ProviderId, message.VisitId, message.PlanId,
            info?.Currency?.Trim() ?? options.Value.DefaultCurrency, null, null, [item], null, clock.UtcNow);
        db.Invoices.Add(Unwrap(created));
    }

    private static T Unwrap<T>(DentaCore.BuildingBlocks.Domain.Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"Cannot build the invoice item: {result.Error!.Code}");

    private static void Unwrap(DentaCore.BuildingBlocks.Domain.Result result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Cannot add the invoice item: {result.Error!.Code}");
        }
    }

    private sealed record ProcedureInfo(string Name, Guid? ServiceId, decimal? VatRate, string? Currency);
}
