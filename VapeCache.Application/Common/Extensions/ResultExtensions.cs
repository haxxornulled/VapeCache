using LanguageExt.Common;
using Microsoft.Extensions.Logging;

namespace VapeCache.Application.Common.Extensions;

public static class ResultExtensions
{
    private const string HandleSuccessFailureMessage = "HandleSuccess was called on a failed Result.";
    private const string HandleSuccessWithLoggingFailureMessage = "HandleSuccessWithLogging was called on a failed Result.";
    private const string ErrorHandlingFailureMessage = "An error occurred while handling or logging another exception.";

    /// <summary>
    /// Handles errors by executing the provided error handler action and returning the caught exception.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="result">The result to handle.</param>
    /// <param name="errorHandler">The action to execute if an error occurs.</param>
    /// <returns>The caught exception if an error occurred; otherwise, null.</returns>
    public static Exception? HandleError<T>(
        this Result<T> result,
        Action<Exception> errorHandler)
    {
        ArgumentNullException.ThrowIfNull(errorHandler);

        return result.Match<Exception?>(
            _ => null,
            error =>
            {
                errorHandler(error);
                return error;
            });
    }

    /// <summary>
    /// Handles errors by executing the provided error handler function and returning the error result.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <typeparam name="TE">The type of the error handler's return value.</typeparam>
    /// <param name="result">The result to handle.</param>
    /// <param name="errorHandler">The function to execute if an error occurs.</param>
    /// <returns>The result of the error handler if an error occurred; otherwise, default(TE).</returns>
    public static TE? HandleError<T, TE>(
        this Result<T> result,
        Func<Exception, TE> errorHandler)
    {
        ArgumentNullException.ThrowIfNull(errorHandler);

        return result.Match<TE?>(
            _ => default,
            errorHandler);
    }

    /// <summary>
    /// Handles successful results by executing the provided success handler action and returning the result value.
    /// If the result is an error, an exception is thrown.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="result">The result to handle.</param>
    /// <param name="successHandler">The action to execute if the result is successful.</param>
    /// <returns>The result value if successful.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the result is an error.</exception>
    public static T HandleSuccess<T>(
        this Result<T> result,
        Action<T> successHandler)
    {
        ArgumentNullException.ThrowIfNull(successHandler);

        return result.Match(
            success =>
            {
                successHandler(success);
                return success;
            },
            ThrowHandleSuccessFailure<T>);
    }

    /// <summary>
    /// Handles successful results by executing the provided success handler function and returning the modified result value.
    /// If the result is an error, an exception is thrown.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="result">The result to handle.</param>
    /// <param name="successHandler">The function to execute if the result is successful.</param>
    /// <returns>The modified result value if successful.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the result is an error.</exception>
    public static T HandleSuccess<T>(
        this Result<T> result,
        Func<T, T> successHandler)
    {
        ArgumentNullException.ThrowIfNull(successHandler);

        return result.Match(
            successHandler,
            ThrowHandleSuccessFailure<T>);
    }

    /// <summary>
    /// Handles errors for a Result object, allowing both logging and custom error-handling actions.
    /// Returns the exception if an error occurred, or null if no error occurred.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="result">The result to handle.</param>
    /// <param name="errorHandler">The action to execute if an error occurs.</param>
    /// <param name="logAction">The action to log the error.</param>
    /// <param name="logger">The logger instance to use for logging.</param>
    /// <returns>The exception if an error occurred; otherwise, null.</returns>
    public static Exception? HandleErrorWithLogging<T>(
        this Result<T> result,
        Action<Exception> errorHandler,
        Action<ILogger, Exception> logAction,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(errorHandler);
        ArgumentNullException.ThrowIfNull(logAction);
        ArgumentNullException.ThrowIfNull(logger);

        return result.Match<Exception?>(
            _ => null,
            error =>
            {
                try
                {
                    logAction(logger, error);
                    errorHandler(error);
                }
                catch (Exception loggingException)
                {
                    logger.LogError(loggingException, ErrorHandlingFailureMessage);
                }

                return error;
            });
    }

    /// <summary>
    /// Handles successful results for a Result object, allowing both logging and custom success-handling actions.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="result">The result to handle.</param>
    /// <param name="successHandler">The action to execute if the result is successful.</param>
    /// <param name="logAction">The action to log the success.</param>
    /// <param name="logger">The logger instance to use for logging.</param>
    /// <returns>The result value if successful.</returns>
    /// <exception cref="InvalidOperationException">Thrown if the result is an error.</exception>
    public static T HandleSuccessWithLogging<T>(
        this Result<T> result,
        Action<T> successHandler,
        Action<ILogger, T> logAction,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(successHandler);
        ArgumentNullException.ThrowIfNull(logAction);
        ArgumentNullException.ThrowIfNull(logger);

        return result.Match(
            success =>
            {
                logAction(logger, success);
                successHandler(success);
                return success;
            },
            ThrowHandleSuccessWithLoggingFailure<T>);
    }

    private static T ThrowHandleSuccessFailure<T>(Exception error) =>
        throw new InvalidOperationException(HandleSuccessFailureMessage, error);

    private static T ThrowHandleSuccessWithLoggingFailure<T>(Exception error) =>
        throw new InvalidOperationException(HandleSuccessWithLoggingFailureMessage, error);
}
