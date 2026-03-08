using LanguageExt.Common;
using Microsoft.Extensions.Logging;

namespace VapeCache.Core.Common.Extensions;

/// <summary>
/// Helper extensions for <see cref="Result{T}"/> success/error handling with optional logging hooks.
/// </summary>
public static class ResultExtensions
{
    private const string HandleSuccessFailureMessage = "HandleSuccess was called on a failed Result.";
    private const string HandleSuccessWithLoggingFailureMessage = "HandleSuccessWithLogging was called on a failed Result.";
    private const string ErrorHandlingFailureMessage = "An error occurred while handling or logging another exception.";

    /// <summary>
    /// Executes an error handler when the result is failed and returns the captured exception.
    /// </summary>
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
    /// Maps a failed result exception to a custom error value.
    /// </summary>
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
    /// Executes a success handler and returns the success value.
    /// Throws when the result is failed.
    /// </summary>
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
    /// Transforms a successful result value and returns the transformed value.
    /// Throws when the result is failed.
    /// </summary>
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
    /// Executes error logging and error handling when the result is failed.
    /// </summary>
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
    /// Executes success logging and success handling for a successful result.
    /// Throws when the result is failed.
    /// </summary>
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
