using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace Application.Common.Extensions;

public static class ResultExtensions
{
    private const string HandleSuccessFailureMessage = "HandleSuccess was called on a failed Result.";
    private const string HandleSuccessWithLoggingFailureMessage = "HandleSuccessWithLogging was called on a failed Result.";
    private const string ErrorHandlingFailureMessage = "An error occurred while handling or logging another exception.";

    public static Option<Exception> HandleError<T>(
        this Result<T> result,
        Action<Exception> errorHandler)
    {
        ArgumentNullException.ThrowIfNull(errorHandler);

        return result.Match(
            _ => Option<Exception>.None,
            error =>
            {
                errorHandler(error);
                return Some(error);
            });
    }

    public static Option<TE> HandleError<T, TE>(
        this Result<T> result,
        Func<Exception, TE> errorHandler)
    {
        ArgumentNullException.ThrowIfNull(errorHandler);

        return result.Match(
            _ => Option<TE>.None,
            error => Some(errorHandler(error)));
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

    public static Option<Exception> HandleErrorWithLogging<T>(
        this Result<T> result,
        Action<Exception> errorHandler,
        Action<ILogger, Exception> logAction,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(errorHandler);
        ArgumentNullException.ThrowIfNull(logAction);
        ArgumentNullException.ThrowIfNull(logger);

        return result.Match(
            _ => Option<Exception>.None,
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

                return Some(error);
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

    public static Result<T> LogFailure<T>(
        this Result<T> result,
        ILogger logger,
        string messageTemplate,
        params object[] propertyValues)
    {
        if (result.IsFaulted)
        {
            result.IfFail(ex => logger.LogError(ex, messageTemplate, propertyValues));
        }

        return result;
    }

    public static Result<T> LogFailureMessage<T>(
        this Result<T> result,
        ILogger logger,
        string messageTemplate,
        params object[] propertyValues)
    {
        if (result.IsFaulted)
        {
            result.IfFail(_ => logger.LogWarning(messageTemplate, propertyValues));
        }

        return result;
    }

    public static Option<T> LogNone<T>(
        this Option<T> option,
        ILogger logger,
        string messageTemplate,
        params object[] propertyValues)
    {
        if (option.IsNone)
        {
            logger.LogWarning(messageTemplate, propertyValues);
        }

        return option;
    }

    public static T ValueOrThrow<T>(
        this Result<T> result,
        Func<Exception, Exception> exceptionFactory) =>
        result.Match(
            value => value,
            ex => throw exceptionFactory(ex));

    public static T ValueOrThrow<T>(
        this Option<T> option,
        Func<Exception> exceptionFactory) =>
        option.Match(
            value => value,
            () => throw exceptionFactory());

    public static T ValueOr<T>(
        this Result<T> result,
        Func<Exception, T> fallbackFactory) =>
        result.Match(
            value => value,
            ex => fallbackFactory(ex));

    public static bool TryGetValue<T>(this Result<T> result, out T value)
    {
        var match = result.Match(
            v => (HasValue: true, Value: v),
            _ => (HasValue: false, Value: default!));

        value = match.Value;
        return match.HasValue;
    }

    private static T ThrowHandleSuccessFailure<T>(Exception error) =>
        throw new InvalidOperationException(HandleSuccessFailureMessage, error);

    private static T ThrowHandleSuccessWithLoggingFailure<T>(Exception error) =>
        throw new InvalidOperationException(HandleSuccessWithLoggingFailureMessage, error);
}
