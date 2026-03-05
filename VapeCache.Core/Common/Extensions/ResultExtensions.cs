using LanguageExt.Common;
using Microsoft.Extensions.Logging;

namespace VapeCache.Core.Common.Extensions;

public static class ResultExtensions
{
    private const string HandleSuccessFailureMessage = "HandleSuccess was called on a failed Result.";
    private const string HandleSuccessWithLoggingFailureMessage = "HandleSuccessWithLogging was called on a failed Result.";
    private const string ErrorHandlingFailureMessage = "An error occurred while handling or logging another exception.";

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

    public static TE? HandleError<T, TE>(
        this Result<T> result,
        Func<Exception, TE> errorHandler)
    {
        ArgumentNullException.ThrowIfNull(errorHandler);

        return result.Match<TE?>(
            _ => default,
            errorHandler);
    }

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

    public static T HandleSuccess<T>(
        this Result<T> result,
        Func<T, T> successHandler)
    {
        ArgumentNullException.ThrowIfNull(successHandler);

        return result.Match(
            successHandler,
            ThrowHandleSuccessFailure<T>);
    }

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
