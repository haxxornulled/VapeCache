namespace VapeCache.Application.Abstractions;

/// <summary>
/// Defines the command handler contract.
/// </summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    /// <summary>
    /// Executes handle async.
    /// </summary>
    ValueTask<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}
