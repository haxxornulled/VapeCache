using System.Runtime.CompilerServices;

namespace VapeCache.Application.Guards;

/// <summary>
/// Provides guard clauses for argument validation.
/// </summary>
public static class Guard
{
    public static class Against
    {
        /// <summary>
        /// Executes value.
        /// </summary>
        public static string NotNullOrEmpty(string? value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException($"{argumentName ?? nameof(value)} cannot be null or empty.", argumentName ?? nameof(value));

            return value;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public static string NotNullOrWhiteSpace(string? value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{argumentName ?? nameof(value)} cannot be null or whitespace.", argumentName ?? nameof(value));

            return value;
        }

        public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
            where T : struct
        {
            if (!value.HasValue)
                throw new ArgumentNullException(argumentName ?? nameof(value));

            return value.Value;
        }

        /// <summary>
        /// Executes value.
        /// </summary>
        public static int NotOutOfRange(int value, int min, int max, [CallerArgumentExpression(nameof(value))] string argumentName = "")
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(argumentName ?? nameof(value), $"Value must be between {min} and {max}. Actual value: {value}");

            return value;
        }

        public static TEnum ValidEnumValue<TEnum>(TEnum value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
            where TEnum : struct, Enum
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentException($"Invalid enum value: {value}", argumentName ?? nameof(value));

            return value;
        }

    }
}
