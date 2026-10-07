using DentaCore.BuildingBlocks.Application;
using Microsoft.EntityFrameworkCore.Storage;

namespace DentaCore.BuildingBlocks.Infrastructure.Outbox;

/// <summary>EF tranzaksiyasını IUnitOfWorkTransaction kimi göstərir (hər modulun DbContext-i istifadə edir).</summary>
public sealed class EfTransaction(IDbContextTransaction inner) : IUnitOfWorkTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
