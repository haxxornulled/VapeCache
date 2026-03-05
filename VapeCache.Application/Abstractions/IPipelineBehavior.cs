namespace VapeCache.Application.Abstractions;

public interface IPipelineBehavior<in TRequest, TResponse>
{
    ValueTask<TResponse> HandleAsync(
        TRequest request,
        Func<CancellationToken, ValueTask<TResponse>> next,
        CancellationToken cancellationToken = default);
}
