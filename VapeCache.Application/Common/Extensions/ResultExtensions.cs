
using LanguageExt;
using LanguageExt.Common;
using Microsoft.Extensions.Logging;

namespace VapeCache.Application.Common.Extensions;

//public static class ResultExtensions
//{
//    /// <summary>
//    /// Handles errors by executing the provided error handler action and returning the caught exception.
//    /// </summary>
//    public static Exception? HandleError<T>(
//        this Result<T> result,
//        Action<Exception> errorHandler)
//    {
//        return result.Match(
//            success => null!, // No error
//            error =>
//            {
//                errorHandler(error);
//                return error; // Return caught exception
//            });
//    }

//    /// <summary>
//    /// Handles errors by executing the provided error handler function and returning the error result.
//    /// </summary>
//    public static TE? HandleError<T, TE>(
//        this Result<T> result,
//        Func<Exception, TE> errorHandler)
//    {
//        return result.Match(
//            success => default!, // No error, return default TE
//            errorHandler);
//    }

//    /// <summary>
//    /// Handles successful results by executing the provided success handler action and returning the result value.
//    /// If the result is an error, an exception is thrown.
//    /// </summary>
//    public static T HandleSuccess<T>(
//        this Result<T> result,
//        Action<T> successHandler)
//    {
//        return result.Match(
//            success =>
//            {
//                successHandler(success);
//                return success;
//            },
//            error => throw new InvalidOperationException("HandleSuccess was called on a failed Result."));
//    }

//    /// <summary>
//    /// Handles successful results by executing the provided success handler function and returning the modified result value.
//    /// If the result is an error, an exception is thrown.
//    /// </summary>
//    public static T HandleSuccess<T>(
//        this Result<T> result,
//        Func<T, T> successHandler)
//    {
//        return result.Match(
//            successHandler,
//            error => throw new InvalidOperationException("HandleSuccess was called on a failed Result."));
//    }

//    /// <summary>
//    /// Handles errors for a Result object, allowing both logging and custom error-handling actions.
//    /// Returns the exception if an error occurred, or null if no error occurred.
//    /// </summary>
//    public static Exception? HandleErrorWithLogging<T>(
//        this Result<T> result,
//        Action<Exception> errorHandler,
//        Action<ILogger, Exception> logAction,
//        ILogger logger)
//    {
//        return result.Match(
//            success => null, // No error, return null
//            error =>
//            {
//                try
//                {
//                    logAction(logger, error); // Log the error
//                    errorHandler(error);      // Run the custom error handler
//                }
//                catch (Exception loggingException)
//                {
//                    logger.LogError(loggingException, "An error occurred while handling or logging another exception.");
//                }
//                return error; // Return the original error
//            });
//    }



//    /// <summary>
//    /// Handles successful results for a Result object, allowing both logging and custom success-handling actions.
//    /// </summary>
//    public static T HandleSuccessWithLogging<T>(
//        this Result<T> result,
//        Action<T> successHandler,
//        Action<ILogger, T> logAction,
//        ILogger logger)
//    {
//        return result.Match(
//            success =>
//            {
//                logAction(logger, success);
//                successHandler(success);
//                return success;
//            },
//            error => throw new InvalidOperationException("HandleSuccessWithLogging was called on a failed Result."));
//    }
//}





public static class ResultExtensions
{
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
        return result.Match(
            success => null, // No error, return null
            error =>
            {
                errorHandler(error);
                return error; // Return caught exception
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
        return result.Match(
            success => default, // No error, return default TE
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
        return result.Match(
            success =>
            {
                successHandler(success);
                return success;
            },
            error => throw new InvalidOperationException("HandleSuccess was called on a failed Result."));
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
        return result.Match(
            successHandler,
            error => throw new InvalidOperationException("HandleSuccess was called on a failed Result."));
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
        return result.Match(
            success => null, // No error, return null
            error =>
            {
                try
                {
                    logAction(logger, error); // Log the error
                    errorHandler(error);      // Run the custom error handler
                }
                catch (Exception loggingException)
                {
                    logger.LogError(loggingException, "An error occurred while handling or logging another exception.");
                }
                return error; // Return the original error
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
        return result.Match(
            success =>
            {
                logAction(logger, success);
                successHandler(success);
                return success;
            },
            error => throw new InvalidOperationException("HandleSuccessWithLogging was called on a failed Result."));
    }
}
