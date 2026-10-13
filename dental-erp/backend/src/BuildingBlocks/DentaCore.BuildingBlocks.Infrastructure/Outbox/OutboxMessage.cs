using System.Text.Json;
using DentaCore.BuildingBlocks.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace DentaCore.BuildingBlocks.Infrastructure.Outbox;

/// <summary>Transactional Outbox sətri (T001: outbox_messages). RabbitMQ publisher (Workers) processed_at null olanları göndərir.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    public string Type { get; init; } = default!;

    public string RoutingKey { get; init; } = default!;

    public string Payload { get; init; } = default!;

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

public static class OutboxModelExtensions
{
    public static void ConfigureOutbox(this ModelBuilder modelBuilder) =>
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id");
            b.Property(x => x.Type).HasColumnName("type");
            b.Property(x => x.RoutingKey).HasColumnName("routing_key");
            b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            b.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            b.Property(x => x.Attempts).HasColumnName("attempts");
            b.Property(x => x.LastError).HasColumnName("last_error");
        });
}

/// <summary>
/// SaveChanges zamanı aggregate-lərin domen hadisələrini eyni tranzaksiyada outbox-a yazır.
/// Beləliklə "DB yazıldı, mesaj itdi" vəziyyəti mümkün deyil.
/// </summary>
public sealed class DomainEventsToOutboxInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var context = eventData.Context;
        if (context is null)
        {
            return ValueTask.FromResult(result);
        }

        var aggregates = context.ChangeTracker.Entries<IAggregateRoot>()
            .Select(e => e.Entity)
            .Where(a => a.DomainEvents.Count > 0)
            .ToList();

        foreach (var aggregate in aggregates)
        {
            foreach (var domainEvent in aggregate.DomainEvents)
            {
                context.Set<OutboxMessage>().Add(new OutboxMessage
                {
                    Id = domainEvent.EventId,
                    Type = domainEvent.GetType().Name,
                    RoutingKey = domainEvent.RoutingKey,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Json),
                    OccurredAt = domainEvent.OccurredAt,
                });
            }

            aggregate.ClearDomainEvents();
        }

        return ValueTask.FromResult(result);
    }
}
