using LanguageExt;
using static LanguageExt.Prelude;
using HashSet = System.Collections.Generic.HashSet<string>;

namespace VapeCache.Application.Guards;

/// <summary>
/// Paranoia: Extreme validation for inputs that could destroy everything if left unchecked.
/// Trust nothing. Validate everything.
/// </summary>
public static class Paranoia
{
    public delegate Task<Validation<TFail, TSuccess>> AsyncValidation<TFail, TSuccess>();

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

    public static Validation<MonoidFail, bool> Validate(string? input, string paramName)
        => string.IsNullOrEmpty(input)
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} cannot be null or empty"))
            : Success<MonoidFail, bool>(true);

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

    public static Task<Validation<MonoidFail, bool>> ValidateAsync(AsyncValidation<MonoidFail, bool> asyncValidation)
        => asyncValidation();

    public static Validation<MonoidFail, bool> ValidateNonNegativeTimeSpan(TimeSpan value, string paramName)
        => value < TimeSpan.Zero
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} must be non-negative"))
            : Success<MonoidFail, bool>(true);

    public static Validation<MonoidFail, bool> ValidateNonNegativeInt(int value, string paramName)
        => value < 0
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} must be non-negative"))
            : Success<MonoidFail, bool>(true);

    public static Validation<MonoidFail, bool> Validate(ReadOnlyMemory<byte> input, string paramName)
        => input.IsEmpty
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} cannot be empty"))
            : Success<MonoidFail, bool>(true);

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

    public static Validation<MonoidFail, bool> Validate<T>(T? input, string paramName) where T : class
        => input is null
            ? Fail<MonoidFail, bool>(MonoidFail.FromError($"{paramName} cannot be null"))
            : Success<MonoidFail, bool>(true);
}
