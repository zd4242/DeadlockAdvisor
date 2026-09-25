namespace DeadlockAdvisor.Enums;

/// <summary>
/// How an item relates to a hero with a trait: bought against an enemy with it, alongside an
/// ally with it, or on a hero of yours with it. The declaration order is the order everything
/// is listed and saved in.
/// </summary>
public enum Relation
{
    Against,
    With,
    As
}

public static class Relations
{
    public static readonly IReadOnlyList<Relation> All = [Relation.Against, Relation.With, Relation.As];

    /// <summary>The spelling the CSVs and the match data use.</summary>
    public static string Key(this Relation relation) => relation switch
    {
        Relation.Against => "against",
        Relation.With => "with",
        Relation.As => "as",
        _ => throw new ArgumentOutOfRangeException(nameof(relation))
    };

    public static string Label(this Relation relation) => relation switch
    {
        Relation.Against => "Against enemy",
        Relation.With => "With ally",
        Relation.As => "As me",
        _ => throw new ArgumentOutOfRangeException(nameof(relation))
    };

    public static string Help(this Relation relation) => relation switch
    {
        Relation.Against => "Buy this when an ENEMY has this trait.",
        Relation.With => "Buy this when an ALLY has this trait.",
        Relation.As => "Buy this when YOUR OWN hero has this trait.",
        _ => throw new ArgumentOutOfRangeException(nameof(relation))
    };

    public static bool TryParse(string? key, out Relation relation)
    {
        switch (key)
        {
            case "against":
                relation = Relation.Against;
                return true;
            case "with":
                relation = Relation.With;
                return true;
            case "as":
                relation = Relation.As;
                return true;
            default:
                relation = default;
                return false;
        }
    }
}
