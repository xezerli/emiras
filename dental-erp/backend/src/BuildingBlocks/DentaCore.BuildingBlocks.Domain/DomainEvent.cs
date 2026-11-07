namespace DentaCore.BuildingBlocks.Domain;

/// <summary>Domen hadisəsi. Keçmiş zamanda adlanır (UserLoggedIn). Aggregate daxilində yaranır, outbox-a yazılır.</summary>
public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEvent(Guid EventId, DateTimeOffset OccurredAt) : IDomainEvent
{
    /// <summary>Outbox-da routing key kimi istifadə olunur: dental.&lt;context&gt;.&lt;event&gt;.</summary>
    public abstract string RoutingKey { get; }
}
