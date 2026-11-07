namespace DentaCore.BuildingBlocks.Domain;

/// <summary>Non-generic görünüş: infrastruktur domen hadisələrini tipdən asılı olmadan toplaya bilsin.</summary>
public interface IAggregateRoot
{
    IReadOnlyCollection<DomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}

/// <summary>
/// Konsistensiya sərhədi. Domen hadisələrini toplayır, infrastruktur onları tranzaksiya daxilində outbox-a yazır.
/// <see cref="RowVersion"/> DB trigger-i ilə artır (optimistic concurrency).
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>, IAggregateRoot
    where TId : notnull
{
    private readonly List<DomainEvent> _events = [];

    protected AggregateRoot(TId id)
        : base(id)
    {
    }

    public int RowVersion { get; private set; }

    public IReadOnlyCollection<DomainEvent> DomainEvents => _events;

    public void ClearDomainEvents() => _events.Clear();

    protected void Raise(DomainEvent domainEvent) => _events.Add(domainEvent);
}
