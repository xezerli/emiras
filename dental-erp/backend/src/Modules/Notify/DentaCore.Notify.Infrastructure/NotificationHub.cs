using System.Security.Claims;
using DentaCore.BuildingBlocks.Infrastructure.Messaging;
using DentaCore.Notify.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace DentaCore.Notify.Infrastructure;

/// <summary>
/// Canlı bildirişlər. Qrup üzvlüyü bağlantı anında token claim-lərindən (tid, perm, branch) hesablanır: klient qrup seçə bilmir.
/// İcazə dəyişəndə yeni token ilə yenidən qoşulmaq lazımdır; token bitəndə bağlantı server tərəfindən bağlanır (CloseOnAuthenticationExpiration).
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    public const string Path = "/hubs/notifications";
    public const string ClientMethod = "notification";

    public override async Task OnConnectedAsync()
    {
        var user = Context.User ?? throw new HubException("unauthenticated");
        if (!Guid.TryParse(user.FindFirstValue("tid"), out var tenantId) || !Guid.TryParse(user.FindFirstValue("sub"), out var userId))
        {
            Context.Abort();
            return;
        }

        var groups = GroupMembership.For(tenantId, userId, user.FindAll("perm").Select(c => c.Value), user.FindAll("branch").Select(c => c.Value));
        foreach (var group in groups)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
        }

        await base.OnConnectedAsync();
    }

    /// <summary>Bu bağlantının hansı sahələri dinlədiyini qaytarır (qrup adlarını yox).</summary>
    public IReadOnlyList<string> GetAreas()
    {
        var user = Context.User!;
        var perms = user.FindAll("perm").Select(c => c.Value.Split('@')[0]).ToHashSet(StringComparer.Ordinal);
        return NotificationAreas.Permissions.Where(p => perms.Contains(p.Value)).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
    }
}

/// <summary>
/// RabbitMQ-dan hadisə alıb SignalR qruplarına ötürür. Broadcast növbəsi: hər Notify nüsxəsi hər mesajı alır,
/// çünki bağlantılar nüsxələr arasında bölünüb və yalnız öz klientlərinə çatdıra bilər (Redis backplane lazım deyil).
/// </summary>
[Consumes("notify.realtime", "scheduling.#", "billing.#", "clinical.visit-closed", Broadcast = true)]
public sealed class RealtimeConsumer(IHubContext<NotificationHub> hub) : IIntegrationConsumer
{
    public async Task HandleAsync(IntegrationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var routed = NotificationRouter.Route(envelope.RoutingKey, envelope.TenantId, envelope.OccurredAt, envelope.Payload);
        if (routed is null)
        {
            return;
        }

        await hub.Clients.Groups(routed.Groups).SendAsync(NotificationHub.ClientMethod, routed.Notification, cancellationToken);
    }
}
