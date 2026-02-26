using LanguageExt.TypeClasses;
using LanguageExt;
using static LanguageExt.Prelude;
namespace VapeCache.Application.Guards;

public sealed class MonoidFail : Monoid<MonoidFail>
{
    public Seq<string> Errors { get; }

    public MonoidFail(Seq<string> errors)
    {
        Errors = errors;
    }

    MonoidFail Monoid<MonoidFail>.Empty() => new(Seq<string>());

    MonoidFail Semigroup<MonoidFail>.Append(MonoidFail x, MonoidFail y) => new(x.Errors.Concat(y.Errors));

    /// <summary>
    /// Executes value.
    /// </summary>
    public static MonoidFail FromError(Seq<string> errors) => new(errors);

    /// <summary>
    /// Executes value.
    /// </summary>
    public static MonoidFail FromError(string error) => new(Seq1(error)); // ✅ Using LanguageExt.Prelude.Seq1
}
