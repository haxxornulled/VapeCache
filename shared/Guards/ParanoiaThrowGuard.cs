using System.Runtime.CompilerServices;

namespace VapeCache.Guards;

/// <summary>
/// Throw-based paranoia guards for extension/runtime boundaries.
/// </summary>
internal static class ParanoiaThrowGuard
{
    internal static class Against
    {
        /// <summary>
        /// Ensures a reference value is not null.
        /// </summary>
        public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string argumentName = "")
            where T : class
        {
            if (value is null)
                throw new ArgumentNullException(argumentName ?? nameof(value));

            return value;
        }

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
    }
}
