using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.Clinical.Contracts;
using DentaCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.Workers.IntegrationTests;

/// <summary>ProcedurePerformed → vizitin qaralama fakturası (real PostgreSQL + RabbitMQ, Workers host-u tam qalxır).</summary>
[Collection(MessagingDefinition.Name)]
public sealed class BillingConsumerTests(MessagingFixture fx)
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

    private sealed record Scene(Guid Patient, Guid Provider, Guid Visit, Guid Plan);

    private async Task<Scene> SeedAsync()
    {
        var patient = await TestData.AddPatientAsync(fx.Db);
        var provider = await fx.Db.SeedUserAsync(TestData.Schema, $"dr-{Guid.NewGuid():N}@clinic.az", "Dr Test", "doctor");
        var branch = await fx.Db.ScalarAsync<Guid>(TestData.Schema, "SELECT branch_id FROM patients WHERE id = $1", patient);
        var visit = Guid.NewGuid();
        var plan = Guid.NewGuid();
        await fx.Db.ExecAsync(TestData.Schema, "INSERT INTO visits(id, patient_id, provider_id, branch_id) VALUES ($1, $2, $3, $4)", visit, patient, provider, branch);
        await fx.Db.ExecAsync(TestData.Schema, "INSERT INTO treatment_plans(id, patient_id, provider_id, title) VALUES ($1, $2, $3, 'Plan')", plan, patient, provider);
        return new Scene(patient, provider, visit, plan);
    }

    private async Task<Guid> PlanItemAsync(Scene s, string code, decimal price, int qty = 1, decimal discount = 0m)
    {
        var id = Guid.NewGuid();
        await fx.Db.ExecAsync(
            TestData.Schema, "INSERT INTO treatment_plan_items(id, plan_id, procedure_code, tooth_fdi, unit_price, quantity, discount_percent) VALUES ($1, $2, $3, 16, $4, $5, $6)",
            id, s.Plan, code, price, qty, discount);
        return id;
    }

    private static ProcedurePerformedV1 Event(Scene s, Guid planItem, string code, decimal price, int qty = 1, decimal discount = 0m) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, s.Plan, planItem, s.Visit, s.Patient, s.Provider, code, 16, qty, price, discount);

    private async Task<int> InvoiceCount(Guid visit) =>
        await fx.Db.ScalarAsync<int>(TestData.Schema, "SELECT count(*)::int FROM invoices WHERE visit_id = $1", visit);

    private async Task<bool> WaitForItems(Guid visit, int expected) =>
        await MessagingFixture.WaitAsync(async () =>
            await fx.Db.ScalarAsync<int>(TestData.Schema, "SELECT count(*)::int FROM invoice_items ii JOIN invoices i ON i.id = ii.invoice_id WHERE i.visit_id = $1", visit) == expected);

    [MessagingFact]
    public async Task Performed_procedures_of_one_visit_build_a_single_draft_invoice()
    {
        await TestData.ResetAsync(fx.Db);
        var s = await SeedAsync();
        var service = Guid.NewGuid();
        await fx.Db.ExecAsync(
            TestData.Schema, "INSERT INTO services(id, code, name, price, vat_rate, procedure_code) VALUES ($1, $2, 'Terapevtik plomb', 999, 18, 'D2391')", service, "S" + service.ToString("N")[..8]);
        var first = await PlanItemAsync(s, "D2391", 80m, qty: 2, discount: 10m);
        var second = await PlanItemAsync(s, "D0120", 20m);
        using var host = StartHost();
        using var client = host.CreateClient();

        await TestData.AddOutboxAsync(fx.Db, "clinical.procedure-performed", Event(s, first, "D2391", 80m, qty: 2, discount: 10m));
        Assert.True(await WaitForItems(s.Visit, 1));
        await TestData.AddOutboxAsync(fx.Db, "clinical.procedure-performed", Event(s, second, "D0120", 20m));
        Assert.True(await WaitForItems(s.Visit, 2));

        Assert.Equal(1, await InvoiceCount(s.Visit));
        var invoice = await fx.Db.ScalarAsync<Guid>(TestData.Schema, "SELECT id FROM invoices WHERE visit_id = $1", s.Visit);
        Assert.Equal("draft", await fx.Db.ScalarAsync<string>(TestData.Schema, "SELECT status FROM invoices WHERE id = $1", invoice));
        Assert.Equal(s.Provider, await fx.Db.ScalarAsync<Guid>(TestData.Schema, "SELECT provider_id FROM invoices WHERE id = $1", invoice));
        Assert.Equal(s.Plan, await fx.Db.ScalarAsync<Guid>(TestData.Schema, "SELECT plan_id FROM invoices WHERE id = $1", invoice));
        // Qiymət icra anındakı plan bəndindən (80), qiymət siyahısından (999) deyil; ƏDV və xidmət bağı siyahıdan
        Assert.Equal(80m, await fx.Db.ScalarAsync<decimal>(TestData.Schema, "SELECT unit_price FROM invoice_items WHERE plan_item_id = $1", first));
        Assert.Equal(16m, await fx.Db.ScalarAsync<decimal>(TestData.Schema, "SELECT discount FROM invoice_items WHERE plan_item_id = $1", first));   // 160 * 10%
        Assert.Equal(18m, await fx.Db.ScalarAsync<decimal>(TestData.Schema, "SELECT vat_rate FROM invoice_items WHERE plan_item_id = $1", first));
        Assert.Equal(service, await fx.Db.ScalarAsync<Guid>(TestData.Schema, "SELECT service_id FROM invoice_items WHERE plan_item_id = $1", first));
        Assert.Equal("Terapevtik plomb", await fx.Db.ScalarAsync<string>(TestData.Schema, "SELECT description FROM invoice_items WHERE plan_item_id = $1", first));
        Assert.Equal("Periodik müayinə", await fx.Db.ScalarAsync<string>(TestData.Schema, "SELECT description FROM invoice_items WHERE plan_item_id = $1", second));   // xidmət yoxdur: prosedur adı
        // (160 - 16) * 1.18 = 169.92 ; + 20
        Assert.Equal(189.92m, await fx.Db.ScalarAsync<decimal>(TestData.Schema, "SELECT total FROM invoices WHERE id = $1", invoice));
    }

    [MessagingFact]
    public async Task Redelivered_and_repeated_events_do_not_double_bill()
    {
        await TestData.ResetAsync(fx.Db);
        var s = await SeedAsync();
        var item = await PlanItemAsync(s, "D2391", 50m);
        var other = await PlanItemAsync(s, "D0120", 10m);
        using var host = StartHost();
        using var client = host.CreateClient();
        var publisher = host.Services.GetRequiredService<IEventPublisher>();
        var envelope = TestData.Envelope(fx.DemoTenantId, "clinical.procedure-performed", Event(s, item, "D2391", 50m));

        await publisher.PublishAsync(envelope, default);
        await publisher.PublishAsync(envelope, default);   // eyni mesaj id: inbox atır
        await publisher.PublishAsync(TestData.Envelope(fx.DemoTenantId, "clinical.procedure-performed", Event(s, item, "D2391", 50m)), default);   // yeni mesaj, eyni plan bəndi: atlanır
        await publisher.PublishAsync(TestData.Envelope(fx.DemoTenantId, "clinical.procedure-performed", Event(s, other, "D0120", 10m)), default);   // sonuncu: consumer canlıdır

        Assert.True(await WaitForItems(s.Visit, 2));
        await Task.Delay(500);
        Assert.Equal(1, await InvoiceCount(s.Visit));
        Assert.Equal(1, await fx.Db.ScalarAsync<int>(TestData.Schema, "SELECT count(*)::int FROM invoice_items WHERE plan_item_id = $1", item));
        Assert.Equal(60m, await fx.Db.ScalarAsync<decimal>(TestData.Schema, "SELECT total FROM invoices WHERE visit_id = $1", s.Visit));
    }

    [MessagingFact]
    public async Task Event_after_the_invoice_was_issued_starts_a_new_draft()
    {
        await TestData.ResetAsync(fx.Db);
        var s = await SeedAsync();
        var first = await PlanItemAsync(s, "D2391", 50m);
        var late = await PlanItemAsync(s, "D0120", 10m);
        using var host = StartHost();
        using var client = host.CreateClient();

        await TestData.AddOutboxAsync(fx.Db, "clinical.procedure-performed", Event(s, first, "D2391", 50m));
        Assert.True(await WaitForItems(s.Visit, 1));
        await fx.Db.ExecAsync(TestData.Schema, "UPDATE invoices SET status = 'issued', issued_at = now(), due_date = current_date WHERE visit_id = $1", s.Visit);
        await TestData.AddOutboxAsync(fx.Db, "clinical.procedure-performed", Event(s, late, "D0120", 10m));
        Assert.True(await WaitForItems(s.Visit, 2));

        Assert.Equal(2, await InvoiceCount(s.Visit));
        Assert.Equal("issued", await fx.Db.ScalarAsync<string>(TestData.Schema, "SELECT status FROM invoices WHERE id = (SELECT invoice_id FROM invoice_items WHERE plan_item_id = $1)", first));
        Assert.Equal("draft", await fx.Db.ScalarAsync<string>(TestData.Schema, "SELECT status FROM invoices WHERE id = (SELECT invoice_id FROM invoice_items WHERE plan_item_id = $1)", late));
    }

    [MessagingFact]
    public async Task Event_for_a_missing_patient_ends_up_in_the_dead_letter_queue()
    {
        await TestData.ResetAsync(fx.Db);
        var s = await SeedAsync();
        var item = await PlanItemAsync(s, "D2391", 50m);
        using var host = StartHost();
        using var client = host.CreateClient();
        await using var channel = await TestData.OpenChannelAsync(fx);
        var ghost = new ProcedurePerformedV1(Guid.NewGuid(), DateTimeOffset.UtcNow, s.Plan, item, s.Visit, Guid.NewGuid(), s.Provider, "D2391", 16, 1, 50m, 0m);
        var envelope = TestData.Envelope(fx.DemoTenantId, "clinical.procedure-performed", ghost);

        await host.Services.GetRequiredService<IEventPublisher>().PublishAsync(envelope, default);

        var dead = await TestData.GetAsync(channel, $"{fx.QueuePrefix}.billing.invoice-draft.dlq");
        Assert.NotNull(dead);
        Assert.Contains(envelope.Id.ToString(), dead.Value.Body, StringComparison.Ordinal);
        Assert.Equal(0, await InvoiceCount(s.Visit));
    }

    [MessagingFact]
    public void Clinical_domain_event_json_is_readable_through_the_published_contract()
    {
        var domainEvent = new Clinical.Domain.ProcedurePerformed(
            Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "D2391", 16, 2, 80m, 10m);

        var json = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), IntegrationEnvelope.Json);
        var contract = JsonSerializer.Deserialize<ProcedurePerformedV1>(json, IntegrationEnvelope.Json)!;

        Assert.Equal(domainEvent.PlanItemId, contract.PlanItemId);
        Assert.Equal(domainEvent.VisitId, contract.VisitId);
        Assert.Equal(domainEvent.PatientId, contract.PatientId);
        Assert.Equal(domainEvent.ProviderId, contract.ProviderId);
        Assert.Equal("D2391", contract.ProcedureCode);
        Assert.Equal(16, contract.ToothFdi);
        Assert.Equal(2, contract.Quantity);
        Assert.Equal(80m, contract.UnitPrice);
        Assert.Equal(10m, contract.DiscountPercent);
    }
}
