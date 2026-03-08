using LanguageExt;
using HashSet = System.Collections.Generic.HashSet<string>;
using static LanguageExt.Prelude;

namespace VapeCache.Core.Guards;

/// <summary>
/// Functional validation helpers built on LanguageExt <c>Validation</c>.
/// </summary>
public static class Paranoia
{
    /// <summary>
    /// Delegate shape for asynchronous validations.
    /// </summary>
    public delegate Task<Validation<TFail, TSuccess>> AsyncValidation<TFail, TSuccess>();

    /// <summary>
    /// Combines multiple boolean validations into a single unit result.
    /// </summary>
    public static Validation<MonoidFail, Unit> Combine(params Validation<MonoidFail, bool>[] validations)
    {
        var acc = Success<MonoidFail, Unit>(unit);
        foreach (var v in validations)
        {
            var next = v.Map(_ => unit);
            acc = from _ in acc
                  from __ in next
                  select unit;
        }
        return acc;
    }

    /// <summary>
    /// Validates that a string input is non-empty.
    /// </summary>
    public static Validation<MonoidFail, bool> Validate(string? input, string paramName)
        => string.IsNullOrEmpty(input)
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} cannot be null or empty"))
            : Success<MonoidFail, bool>(true);

    /// <summary>
    /// Validates that a string array contains at least one non-empty unique entry.
    /// </summary>
    public static Validation<MonoidFail, bool> Validate(string[]? input, string paramName)
    {
        if (input is null || input.Length == 0)
            return Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} array cannot be null or empty"));

        var seen = new HashSet(StringComparer.Ordinal);
        var any = false;
        foreach (var item in input)
        {
            if (string.IsNullOrWhiteSpace(item))
                continue;

            if (seen.Add(item))
                any = true;
        }

        return any
            ? Success<MonoidFail, bool>(true)
            : Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} array cannot be null, empty, or contain only empty entries"));
    }

    /// <summary>
    /// Validates that a dictionary is non-empty and has non-empty keys.
    /// </summary>
    public static Validation<MonoidFail, bool> Validate<T>(Dictionary<string, T>? input, string paramName)
    {
        if (input is null || input.Count == 0)
            return Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} dictionary cannot be null or empty"));

        foreach (var key in input.Keys)
        {
            if (string.IsNullOrEmpty(key))
                return Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} contains null or empty keys"));
        }

        return Success<MonoidFail, bool>(true);
    }

    /// <summary>
    /// Executes an asynchronous validation delegate.
    /// </summary>
    public static Task<Validation<MonoidFail, bool>> ValidateAsync(AsyncValidation<MonoidFail, bool> asyncValidation)
        => asyncValidation();

    /// <summary>
    /// Validates that a <see cref="TimeSpan"/> is non-negative.
    /// </summary>
    public static Validation<MonoidFail, bool> ValidateNonNegativeTimeSpan(TimeSpan value, string paramName)
        => value < TimeSpan.Zero
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} must be non-negative"))
            : Success<MonoidFail, bool>(true);

    /// <summary>
    /// Validates that an integer is non-negative.
    /// </summary>
    public static Validation<MonoidFail, bool> ValidateNonNegativeInt(int value, string paramName)
        => value < 0
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} must be non-negative"))
            : Success<MonoidFail, bool>(true);

    /// <summary>
    /// Validates that a byte memory payload is non-empty.
    /// </summary>
    public static Validation<MonoidFail, bool> Validate(ReadOnlyMemory<byte> input, string paramName)
        => input.IsEmpty
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} cannot be empty"))
            : Success<MonoidFail, bool>(true);

    /// <summary>
    /// Validates that a memory segment array is non-empty and contains no empty segments.
    /// </summary>
    public static Validation<MonoidFail, bool> Validate(ReadOnlyMemory<byte>[]? input, string paramName)
    {
        if (input is null || input.Length == 0)
            return Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} array cannot be null or empty"));

        foreach (var v in input)
        {
            if (v.IsEmpty)
                return Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} contains empty items"));
        }

        return Success<MonoidFail, bool>(true);
    }

    /// <summary>
    /// Validates that a reference type input is not null.
    /// </summary>
    public static Validation<MonoidFail, bool> Validate<T>(T? input, string paramName) where T : class
        => input is null
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} cannot be null"))
            : Success<MonoidFail, bool>(true);
}
