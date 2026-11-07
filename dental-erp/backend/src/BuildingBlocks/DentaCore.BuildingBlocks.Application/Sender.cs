using System.Collections.Concurrent;
using DentaCore.BuildingBlocks.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace DentaCore.BuildingBlocks.Application;

/// <summary>
/// Yüngül dispatcher (MediatR əvəzi: lisenziya və asılılıq riski yoxdur).
/// Handler-i DI-dan tapır, behavior-ları ilk qeydiyyat = ən xarici olmaqla sarıyır.
/// </summary>
internal sealed class Sender(IServiceProvider services) : ISender
{
    private static readonly ConcurrentDictionary<Type, HandlerInvoker> Invokers = new();

    public Task<Result<TResponse>> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invoker = (HandlerInvoker<TResponse>)Invokers.GetOrAdd(
            request.GetType(),
            static type => (HandlerInvoker)Activator.CreateInstance(
                typeof(HandlerInvoker<,>).MakeGenericType(type, GetResponseType(type)))!);
        return invoker.Invoke(request, services, cancellationToken);
    }

    private static Type GetResponseType(Type requestType) =>
        requestType.GetInterfaces()
            .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequest<>))
            .GetGenericArguments()[0];

    private abstract class HandlerInvoker
    {
    }

    private abstract class HandlerInvoker<TResponse> : HandlerInvoker
    {
        public abstract Task<Result<TResponse>> Invoke(IRequest<TResponse> request, IServiceProvider sp, CancellationToken ct);
    }

    private sealed class HandlerInvoker<TRequest, TResponse> : HandlerInvoker<TResponse>
        where TRequest : IRequest<TResponse>
    {
        public override Task<Result<TResponse>> Invoke(IRequest<TResponse> request, IServiceProvider sp, CancellationToken ct)
        {
            var handler = sp.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
            var behaviors = sp.GetServices<IPipelineBehavior<TRequest, TResponse>>().ToArray();
            var typed = (TRequest)request;

            PipelineNext<TResponse> pipeline = () => handler.Handle(typed, ct);
            for (var i = behaviors.Length - 1; i >= 0; i--)
            {
                var behavior = behaviors[i];
                var next = pipeline;
                pipeline = () => behavior.Handle(typed, next, ct);
            }

            return pipeline();
        }
    }
}
