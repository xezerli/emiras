using System.Text.Json;
using DentaCore.Notify.Application;

namespace DentaCore.Notify.UnitTests;

public sealed class RoutingTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Web);

    // ---------------- Router ----------------
    [Fact]
    public void Appointment_event_goes_to_tenant_branch_and_provider_groups_with_ids_only()
    {
        var appointment = Guid.NewGuid();
        var patient = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var provider = Guid.NewGuid();

        var routed = NotificationRouter.Route("scheduling.appointment-booked", Tenant, Now, Json(new { appointmentId = appointment, patientId = patient, branchId = branch, providerId = provider, start = Now }))!;

        Assert.Equal(new Notification("scheduling.appointment-booked", appointment, patient, branch, Now), routed.Notification);
        Assert.Equal(
            [GroupNames.Tenant(Tenant, "appointments"), GroupNames.Branch(Tenant, "appointments", branch), GroupNames.User(Tenant, "appointments", provider)],
            routed.Groups);
    }

    [Fact]
    public void Notification_never_carries_amounts_or_other_payload_content()
    {
        var routed = NotificationRouter.Route(
            "billing.payment-received", Tenant, Now,
            Json(new { invoiceId = Guid.NewGuid(), patientId = Guid.NewGuid(), branchId = Guid.NewGuid(), amount = 123.45m, method = "cash", fullName = "Aysel Əliyeva" }))!;

        var wire = JsonSerializer.Serialize(routed.Notification);
        Assert.DoesNotContain("123.45", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("Aysel", wire, StringComparison.Ordinal);
        Assert.DoesNotContain("cash", wire, StringComparison.Ordinal);
        Assert.Contains(GroupNames.Tenant(Tenant, "billing"), routed.Groups);
    }

    [Theory]
    [InlineData("scheduling.patient-checked-in", "appointments")]
    [InlineData("billing.invoice-issued", "billing")]
    [InlineData("clinical.visit-closed", "clinical")]
    public void Area_follows_the_routing_key_prefix(string key, string area)
    {
        var routed = NotificationRouter.Route(key, Tenant, Now, Json(new { branchId = Guid.NewGuid() }))!;

        Assert.StartsWith(GroupNames.Tenant(Tenant, area), routed.Groups[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("identity.user-created")]
    [InlineData("audit.recorded")]
    [InlineData("")]
    public void Unknown_areas_are_not_routed(string key) =>
        Assert.Null(NotificationRouter.Route(key, Tenant, Now, Json(new { branchId = Guid.NewGuid() })));

    [Fact]
    public void Payload_without_ids_still_routes_to_the_tenant_group_only()
    {
        var routed = NotificationRouter.Route("billing.invoice-issued", Tenant, Now, Json(new { total = 5 }))!;

        Assert.Equal([GroupNames.Tenant(Tenant, "billing")], routed.Groups);
        Assert.Null(routed.Notification.EntityId);
    }

    [Fact]
    public void Garbage_payload_does_not_throw()
    {
        var routed = NotificationRouter.Route("billing.invoice-issued", Tenant, Now, JsonDocument.Parse("\"text\"").RootElement)!;

        Assert.Single(routed.Groups);
    }

    [Fact]
    public void Non_guid_ids_are_ignored_instead_of_throwing()
    {
        var routed = NotificationRouter.Route("scheduling.appointment-booked", Tenant, Now, Json(new { appointmentId = "not-a-guid", branchId = 5 }))!;

        Assert.Null(routed.Notification.EntityId);
        Assert.Null(routed.Notification.BranchId);
    }

    [Fact]
    public void Group_names_embed_the_tenant_so_tenants_never_share_a_group()
    {
        var branch = Guid.NewGuid();

        Assert.NotEqual(GroupNames.Branch(Tenant, "billing", branch), GroupNames.Branch(Other, "billing", branch));
        Assert.NotEqual(GroupNames.Tenant(Tenant, "billing"), GroupNames.Tenant(Other, "billing"));
    }

    // ---------------- Membership ----------------
    [Fact]
    public void Tenant_scope_joins_one_group_per_permitted_area()
    {
        var groups = GroupMembership.For(Tenant, Guid.NewGuid(), ["appointment:read@tenant", "invoice:read@tenant"], ["*"]);

        Assert.Equal([GroupNames.Tenant(Tenant, "appointments"), GroupNames.Tenant(Tenant, "billing")], groups.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Branch_scope_joins_only_the_users_branches()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var groups = GroupMembership.For(Tenant, Guid.NewGuid(), ["invoice:read@branch"], [a.ToString(), b.ToString()]);

        Assert.Equal(new[] { GroupNames.Branch(Tenant, "billing", a), GroupNames.Branch(Tenant, "billing", b) }.Order(StringComparer.Ordinal), groups.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Branch_scope_with_all_branches_joins_the_tenant_group()
    {
        var groups = GroupMembership.For(Tenant, Guid.NewGuid(), ["invoice:read@branch"], ["*"]);

        Assert.Equal([GroupNames.Tenant(Tenant, "billing")], groups);
    }

    [Fact]
    public void Own_scope_joins_only_the_personal_group()
    {
        var user = Guid.NewGuid();

        var groups = GroupMembership.For(Tenant, user, ["appointment:read@own"], ["*"]);

        Assert.Equal([GroupNames.User(Tenant, "appointments", user)], groups);
    }

    [Fact]
    public void Highest_scope_wins_when_a_permission_appears_twice()
    {
        var groups = GroupMembership.For(Tenant, Guid.NewGuid(), ["invoice:read@own", "invoice:read@tenant", "invoice:read@branch"], [Guid.NewGuid().ToString()]);

        Assert.Equal([GroupNames.Tenant(Tenant, "billing")], groups);
    }

    [Fact]
    public void Users_without_the_read_permission_join_nothing_and_junk_claims_are_ignored()
    {
        var groups = GroupMembership.For(Tenant, Guid.NewGuid(), ["patient:read@tenant", "invoice:write@tenant", "invoice:read", "invoice:read@galaxy", "@tenant"], ["not-a-guid"]);

        Assert.Empty(groups);
    }
}
