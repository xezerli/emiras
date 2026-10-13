using System.Net;
using DentaCore.TestSupport;
using System.Text.Json;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using static DentaCore.Notify.IntegrationTests.NotifyFixture;

namespace DentaCore.Notify.IntegrationTests;

[Collection(NotifyDefinition.Name)]
public sealed class RealtimeTests(NotifyFixture f)
{
    private static string[] Perms(params string[] p) => p;

    private static async Task<Client> ListenAsync(NotifyFixture f, DentaCore.TestSupport.ApiFactory<Program> host, string token, bool otherTenant = false, bool longPolling = false)
    {
        var client = await NotifyFixture.ConnectAsync(host, token, otherTenant, longPolling);
        client.Listen();
        return client;
    }

    private static async Task PublishAsync(DentaCore.TestSupport.ApiFactory<Program> host, IntegrationEnvelope envelope) =>
        await host.Services.GetRequiredService<IEventPublisher>().PublishAsync(envelope, default);

    private static async Task Settle() => await Task.Delay(400);   // mənfi yoxlamadan əvvəl: eyni mesaj artıq çatdırılıbsa digərlərinə də çatıb

    private static Guid IdOf(JsonElement n, string property) => n.GetProperty(property).GetGuid();

    // ---------------- Connection ----------------
    [NotifyFact]
    public void Every_route_requires_authorization_except_the_health_probe()
    {
        using var host = f.StartHost();

        Assert.Equal(["GET /health"], EndpointSecurity.AnonymousRoutes(host.Services));
        Assert.True(EndpointSecurity.RouteCount(host.Services) >= 2, "the hub route must be enumerated");
    }

    [NotifyFact]
    public async Task Connection_without_a_valid_token_is_refused()
    {
        using var host = f.StartHost();
        _ = host.Server;

        await Assert.ThrowsAnyAsync<Exception>(() => NotifyFixture.ConnectAsync(host, "not-a-jwt"));
        using var anonymous = host.CreateClientFor("demo");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/hubs/notifications/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [NotifyFact]
    public async Task Token_of_another_tenant_cannot_connect_through_this_tenants_host_header()
    {
        using var host = f.StartHost();
        _ = host.Server;
        var otherTenantToken = f.Token(Perms("appointment:read@tenant"), otherTenant: true);

        // X-Tenant: demo + token tid=other → TenantClaimGuard rədd edir
        await Assert.ThrowsAnyAsync<Exception>(() => NotifyFixture.ConnectAsync(host, otherTenantToken, otherTenant: false));
    }

    [NotifyFact]
    public async Task Event_reaches_a_subscribed_client_over_websocket_with_the_token_in_the_query_string()
    {
        using var host = f.StartHost();
        await using var client = await ListenAsync(f, host, f.Token(Perms("appointment:read@tenant")));
        var appointment = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var branch = Guid.NewGuid();

        await PublishAsync(host, f.Envelope("scheduling.appointment-booked", new { appointmentId = appointment, patientId = patient, branchId = branch, providerId = Guid.NewGuid() }));

        Assert.True(await WaitAsync(() => !client.Received.IsEmpty));
        client.Received.TryPeek(out var n);
        Assert.Equal("scheduling.appointment-booked", n.GetProperty("event").GetString());
        Assert.Equal(appointment, IdOf(n, "entityId"));
        Assert.Equal(patient, IdOf(n, "patientId"));
        Assert.Equal(branch, IdOf(n, "branchId"));
    }

    [NotifyFact]
    public async Task Long_polling_fallback_works_with_the_authorization_header()
    {
        using var host = f.StartHost();
        await using var client = await ListenAsync(f, host, f.Token(Perms("invoice:read@tenant")), longPolling: true);

        await PublishAsync(host, f.Envelope("billing.invoice-issued", new { invoiceId = Guid.NewGuid(), branchId = Guid.NewGuid() }));

        Assert.True(await WaitAsync(() => !client.Received.IsEmpty));
    }

    // ---------------- Isolation ----------------
    [NotifyFact]
    public async Task Tenants_never_see_each_others_events()
    {
        using var host = f.StartHost();
        await using var demo = await ListenAsync(f, host, f.Token(Perms("appointment:read@tenant")));
        await using var other = await ListenAsync(f, host, f.Token(Perms("appointment:read@tenant"), otherTenant: true), otherTenant: true);

        await PublishAsync(host, f.Envelope("scheduling.appointment-booked", new { appointmentId = Guid.NewGuid(), branchId = Guid.NewGuid() }));
        Assert.True(await WaitAsync(() => !demo.Received.IsEmpty));
        await Settle();
        Assert.Empty(other.Received);

        await PublishAsync(host, f.Envelope("scheduling.appointment-booked", new { appointmentId = Guid.NewGuid(), branchId = Guid.NewGuid() }, otherTenant: true));
        Assert.True(await WaitAsync(() => !other.Received.IsEmpty));
        await Settle();
        Assert.Single(demo.Received);
        Assert.Single(other.Received);
    }

    [NotifyFact]
    public async Task Branch_scope_only_sees_its_own_branch_and_tenant_scope_sees_all()
    {
        using var host = f.StartHost();
        var branchX = Guid.NewGuid();
        var branchY = Guid.NewGuid();
        await using var userX = await ListenAsync(f, host, f.Token(Perms("appointment:read@branch"), branches: [branchX]));
        await using var userY = await ListenAsync(f, host, f.Token(Perms("appointment:read@branch"), branches: [branchY]));
        await using var manager = await ListenAsync(f, host, f.Token(Perms("appointment:read@tenant")));

        await PublishAsync(host, f.Envelope("scheduling.patient-checked-in", new { appointmentId = Guid.NewGuid(), patientId = Guid.NewGuid(), providerId = Guid.NewGuid(), branchId = branchX }));

        Assert.True(await WaitAsync(() => !userX.Received.IsEmpty && !manager.Received.IsEmpty));
        await Settle();
        Assert.Empty(userY.Received);
        Assert.Single(userX.Received);
        Assert.Single(manager.Received);
    }

    [NotifyFact]
    public async Task Events_are_split_by_permission_area()
    {
        using var host = f.StartHost();
        await using var reception = await ListenAsync(f, host, f.Token(Perms("appointment:read@tenant")));
        await using var cashier = await ListenAsync(f, host, f.Token(Perms("invoice:read@tenant")));
        await using var noRelevantPermission = await ListenAsync(f, host, f.Token(Perms("patient:read@tenant", "invoice:write@tenant")));

        await PublishAsync(host, f.Envelope("billing.payment-received", new { invoiceId = Guid.NewGuid(), paymentId = Guid.NewGuid(), branchId = Guid.NewGuid(), amount = 250.75m, method = "cash" }));
        await PublishAsync(host, f.Envelope("scheduling.appointment-cancelled", new { appointmentId = Guid.NewGuid(), branchId = Guid.NewGuid() }));

        Assert.True(await WaitAsync(() => !cashier.Received.IsEmpty && !reception.Received.IsEmpty));
        await Settle();
        Assert.Equal("billing.payment-received", Assert.Single(cashier.Received).GetProperty("event").GetString());
        Assert.Equal("scheduling.appointment-cancelled", Assert.Single(reception.Received).GetProperty("event").GetString());
        Assert.Empty(noRelevantPermission.Received);
    }

    [NotifyFact]
    public async Task Own_scope_doctor_only_gets_his_own_appointments()
    {
        using var host = f.StartHost();
        var drA = Guid.NewGuid();
        var drB = Guid.NewGuid();
        await using var a = await ListenAsync(f, host, f.Token(Perms("appointment:read@own"), userId: drA, branches: [Guid.NewGuid()]));
        await using var b = await ListenAsync(f, host, f.Token(Perms("appointment:read@own"), userId: drB, branches: [Guid.NewGuid()]));

        await PublishAsync(host, f.Envelope("scheduling.appointment-booked", new { appointmentId = Guid.NewGuid(), providerId = drA, branchId = Guid.NewGuid() }));

        Assert.True(await WaitAsync(() => !a.Received.IsEmpty));
        await Settle();
        Assert.Empty(b.Received);
    }

    [NotifyFact]
    public async Task Notification_carries_ids_only_never_amounts_or_names()
    {
        using var host = f.StartHost();
        await using var cashier = await ListenAsync(f, host, f.Token(Perms("invoice:read@tenant")));

        await PublishAsync(host, f.Envelope("billing.payment-received", new { invoiceId = Guid.NewGuid(), branchId = Guid.NewGuid(), amount = 987.65m, method = "card", fullName = "Aysel Əliyeva" }));

        Assert.True(await WaitAsync(() => !cashier.Received.IsEmpty));
        cashier.Received.TryPeek(out var n);
        var wire = n.GetRawText();
        Assert.DoesNotContain("987", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Aysel", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("card", wire, StringComparison.Ordinal);
        Assert.Equal(["at", "branchId", "entityId", "event", "patientId"], n.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [NotifyFact]
    public async Task Hub_reports_which_areas_the_connection_listens_to()
    {
        using var host = f.StartHost();
        await using var client = await ListenAsync(f, host, f.Token(Perms("appointment:read@branch", "invoice:read@own", "patient:read@tenant"), branches: [Guid.NewGuid()]));

        var areas = await client.Connection.InvokeAsync<string[]>("GetAreas");

        Assert.Equal(["appointments", "billing"], areas);
    }

    // ---------------- Delivery model ----------------
    [NotifyFact]
    public async Task Every_host_instance_delivers_to_its_own_clients()
    {
        using var first = f.StartHost();
        using var second = f.StartHost();
        await using var onFirst = await ListenAsync(f, first, f.Token(Perms("appointment:read@tenant")));
        await using var onSecond = await ListenAsync(f, second, f.Token(Perms("appointment:read@tenant")));

        await PublishAsync(first, f.Envelope("scheduling.appointment-booked", new { appointmentId = Guid.NewGuid(), branchId = Guid.NewGuid() }));

        Assert.True(await WaitAsync(() => !onFirst.Received.IsEmpty && !onSecond.Received.IsEmpty));   // bir növbəni bölüşsəydilər yalnız biri alardı
    }

    [NotifyFact]
    public async Task Unknown_tenant_garbage_and_unrelated_events_do_not_stop_delivery()
    {
        using var host = f.StartHost();
        await using var client = await ListenAsync(f, host, f.Token(Perms("appointment:read@tenant")));
        var connection = new RabbitMQ.Client.ConnectionFactory { Uri = new Uri(f.AmqpUri) };
        await using var amqp = await connection.CreateConnectionAsync();
        await using var channel = await amqp.CreateChannelAsync();

        await channel.BasicPublishAsync(f.Exchange, "scheduling.appointment-booked", false, new RabbitMQ.Client.BasicProperties(), System.Text.Encoding.UTF8.GetBytes("not json at all"));
        await PublishAsync(host, f.Envelope("scheduling.appointment-booked", new { appointmentId = Guid.NewGuid(), branchId = Guid.NewGuid() }, slug: "no-such-tenant"));
        await PublishAsync(host, f.Envelope("billing.invoice-issued", new { invoiceId = Guid.NewGuid() }));   // bu klientin sahəsi deyil
        var marker = Guid.NewGuid();
        await PublishAsync(host, f.Envelope("scheduling.appointment-booked", new { appointmentId = marker, branchId = Guid.NewGuid() }));

        Assert.True(await WaitAsync(() => client.Received.Any(m => IdOf(m, "entityId") == marker)));
        Assert.Single(client.Received);
    }

    [NotifyFact]
    public async Task Connection_is_closed_by_the_server_when_the_token_expires()
    {
        using var host = f.StartHost();
        await using var client = await ListenAsync(f, host, f.Token(Perms("appointment:read@tenant"), lifetime: TimeSpan.FromSeconds(4)));

        var closed = await Task.WhenAny(client.Closed, Task.Delay(TimeSpan.FromSeconds(45)));

        Assert.Same(client.Closed, closed);
    }
}
