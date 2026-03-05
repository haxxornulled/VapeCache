using LanguageExt;
using LanguageExt.TypeClasses;
using static LanguageExt.Prelude;

namespace VapeCache.Core.Guards;

public sealed class MonoidFail : Monoid<MonoidFail>
{
    public Seq<string> Errors { get; }

    public MonoidFail(Seq<string> errors)
    {
        Errors = errors;
    }

    MonoidFail Monoid<MonoidFail>.Empty() => new(Seq<string>());

    MonoidFail Semigroup<MonoidFail>.Append(MonoidFail x, MonoidFail y) => new(x.Errors.Concat(y.Errors));

    public static MonoidFail FromError(Seq<string> errors) => new(errors);

    public static MonoidFail FromError(string error) => new(Seq1(error));
}
