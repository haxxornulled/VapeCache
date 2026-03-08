namespace VapeCache.Application.Abstractions;

/// <summary>
/// Defines the pipeline behavior contract.
/// </summary>
public interface IPipelineBehavior<in TRequest, TResponse>
{
    /// <summary>
    /// Executes handle async.
    /// </summary>
    ValueTask<TResponse> HandleAsync(
        TRequest request,
        Func<CancellationToken, ValueTask<TResponse>> next,
        CancellationToken cancellationToken = default);
}
