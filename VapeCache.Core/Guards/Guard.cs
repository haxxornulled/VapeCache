using System.Runtime.CompilerServices;

namespace VapeCache.Core.Guards;

/// <summary>
/// Lightweight argument-guard helpers for common validation checks.
/// </summary>
public static class Guard
{
    /// <summary>
    /// Guard methods that throw on invalid input.
    /// </summary>
    public static class Against
    {
        /// <summary>
        /// Ensures a string is not null or empty.
        /// </summary>
        public static string NotNullOrEmpty(string? value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException($"{argumentName ?? nameof(value)} cannot be null or empty.", argumentName ?? nameof(value));

            return value;
        }

        /// <summary>
        /// Ensures a string is not null, empty, or whitespace.
        /// </summary>
        public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{argumentName ?? nameof(value)} cannot be null or whitespace.", argumentName ?? nameof(value));

            return value;
        }

        /// <summary>
        /// Ensures a nullable value type has a value and returns the value.
        /// </summary>
        public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
            where T : struct
        {
            if (!value.HasValue)
                throw new ArgumentNullException(argumentName ?? nameof(value));

            return value.Value;
        }

        /// <summary>
        /// Ensures an integer falls within the inclusive range.
        /// </summary>
        public static int NotOutOfRange(int value, int min, int max, [CallerArgumentExpression(nameof(value))] string argumentName = "")
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(argumentName ?? nameof(value), $"Value must be between {min} and {max}. Actual value: {value}");

            return value;
        }

        /// <summary>
        /// Ensures an enum value is defined on its enum type.
        /// </summary>
        public static TEnum ValidEnumValue<TEnum>(TEnum value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
            where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentException($"Invalid enum value: {value}", argumentName ?? nameof(value));

            return value;
        }
    }
}
