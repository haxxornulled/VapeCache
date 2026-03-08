using LanguageExt;
using LanguageExt.TypeClasses;
using static LanguageExt.Prelude;

namespace VapeCache.Core.Guards;

/// <summary>
/// Validation failure accumulator that composes multiple error messages.
/// </summary>
public sealed class MonoidFail : Monoid<MonoidFail>
{
    /// <summary>
    /// Gets the accumulated error messages.
    /// </summary>
    public Seq<string> Errors { get; }

    /// <summary>
    /// Initializes the failure aggregate with an error sequence.
    /// </summary>
    public MonoidFail(Seq<string> errors)
    {
        Errors = errors;
    }

    MonoidFail Monoid<MonoidFail>.Empty() => new(Seq<string>());

    MonoidFail Semigroup<MonoidFail>.Append(MonoidFail x, MonoidFail y) => new(x.Errors.Concat(y.Errors));

    /// <summary>
    /// Creates a failure aggregate from a sequence of errors.
    /// </summary>
    public static MonoidFail FromError(Seq<string> errors) => new(errors);

    /// <summary>
    /// Creates a failure aggregate from a single error.
    /// </summary>
    public static MonoidFail FromError(string error) => new(Seq1(error));
}
