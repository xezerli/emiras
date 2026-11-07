using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.BuildingBlocks.Application;

/// <summary>CQRS: sorğu/əmr marker interfeysləri. Command vəziyyəti dəyişir, Query dəyişmir.</summary>
public interface IRequest<TResponse>
{
}

public interface ICommand<TResponse> : IRequest<TResponse>
{
}

public interface IQuery<TResponse> : IRequest<TResponse>
{
}

public interface IRequestHandler<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<Result<TResponse>> Handle(TRequest request, CancellationToken cancellationToken);
}

public delegate Task<Result<TResponse>> PipelineNext<TResponse>();

/// <summary>Pipeline: hər sorğunun ətrafında çarpaz məsələlər (validasiya, log, tranzaksiya).</summary>
public interface IPipelineBehavior<in TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    Task<Result<TResponse>> Handle(TRequest request, PipelineNext<TResponse> next, CancellationToken cancellationToken);
}

public interface ISender
{
    Task<Result<TResponse>> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}

/// <summary>Void nəticəli əmrlər üçün: Result&lt;Unit&gt;.</summary>
public readonly record struct Unit;

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Bir neçə əməliyyatın atomik olması lazım olanda (məs. refresh token rotasiyası).</summary>
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Bir host-da bir neçə modul işləyə bilər, hər modulun öz DbContext-i var.
/// Pipeline əmri hansı modulun Application assembly-sinə aiddirsə, onun UnitOfWork-unu seçir.
/// </summary>
public interface IModuleUnitOfWork : IUnitOfWork
{
    System.Reflection.Assembly ApplicationAssembly { get; }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Cari sorğunun tenant-ı. Gateway/middleware tərəfindən doldurulur.</summary>
public interface ITenantContext
{
    bool IsResolved { get; }

    Guid TenantId { get; }

    string Slug { get; }

    string Schema { get; }
}
