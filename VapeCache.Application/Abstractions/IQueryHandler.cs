namespace VapeCache.Application.Abstractions;

/// <summary>
/// Defines the query handler contract.
/// </summary>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    /// <summary>
    /// Executes handle async.
    /// </summary>
    ValueTask<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}
