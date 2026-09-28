using Avalonia.Media;
using DeadlockAdvisor.Enums;

namespace DeadlockAdvisor.Theme;

/// <summary>
/// Warm charcoal and gold, loosely after Deadlock's own UI; teams read as green (ally) and red
/// (enemy) everywhere. The one source of these colours: Themes/DarkTheme.axaml exposes them as
/// brushes, and the custom-drawn controls read them here.
/// </summary>
public static class Palette
{
    public static readonly Color Bg = Color.Parse("#131317");
    public static readonly Color Surface = Color.Parse("#1a1a20");
    public static readonly Color Surface2 = Color.Parse("#22222a");
    public static readonly Color Surface3 = Color.Parse("#2c2c36");
    public static readonly Color Surface4 = Color.Parse("#373744");
    public static readonly Color Border = Color.Parse("#34343f");
    public static readonly Color BorderStrong = Color.Parse("#4a4a5a");
    public static readonly Color Text = Color.Parse("#eceae6");
    public static readonly Color TextDim = Color.Parse("#a2a0a9");
    public static readonly Color TextFaint = Color.Parse("#6d6b77");
    public static readonly Color Accent = Color.Parse("#e0a745");
    public static readonly Color AccentDim = Color.Parse("#a87c31");
    public static readonly Color Ally = Color.Parse("#45b585");
    public static readonly Color Enemy = Color.Parse("#e2564e");
    public static readonly Color Self = Accent;
    public static readonly Color Positive = Ally;
    public static readonly Color Negative = Enemy;

    /// <summary>The formula's opinion of an item, wherever it's told apart from the match data's.</summary>
    public static readonly Color Formula = Accent;

    /// <summary>Real-match data, where it's drawn beside the formula's gold.</summary>
    public static readonly Color Data = Color.Parse("#5bbfc7");

    public static readonly Color Tier1 = Color.Parse("#7d8590");
    public static readonly Color Tier2 = Color.Parse("#4a9dd6");
    public static readonly Color Tier3 = Color.Parse("#a86fd6");
    public static readonly Color Tier4 = Color.Parse("#e0a745");

    public static readonly Color ShopWeapon = Color.Parse("#e2914e");
    public static readonly Color ShopVitality = Color.Parse("#45b585");
    public static readonly Color ShopSpirit = Color.Parse("#9d7de0");

    // Windows' own close-button reds, so the one destructive caption button reads as usual.
    public static readonly Color CloseHover = Color.Parse("#c42b1c");
    public static readonly Color ClosePressed = Color.Parse("#a8281c");

    public static Color TierColor(int tier) => tier switch
    {
        1 => Tier1,
        2 => Tier2,
        3 => Tier3,
        4 => Tier4,
        _ => TextDim,
    };

    public static Color ShopColor(string category) => category switch
    {
        "weapon" => ShopWeapon,
        "vitality" => ShopVitality,
        "spirit" => ShopSpirit,
        _ => TextFaint,
    };

    public static Color RoleColor(Role role) => role switch
    {
        Role.Ally => Ally,
        Role.Enemy => Enemy,
        Role.Self => Accent,
        _ => Border,
    };

    public static Color RelationColor(Relation relation) => relation switch
    {
        Relation.Against => Enemy,
        Relation.With => Ally,
        _ => Accent,
    };

    public static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>Blend two colours: t=0 gives a, t=1 gives b.</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t, MidpointRounding.ToEven),
        (byte)Math.Round(a.G + (b.G - a.G) * t, MidpointRounding.ToEven),
        (byte)Math.Round(a.B + (b.B - a.B) * t, MidpointRounding.ToEven));

    public static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";
}
