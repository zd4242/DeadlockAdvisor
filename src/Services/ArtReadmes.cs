namespace DeadlockAdvisor.Services;

/// <summary>The README dropped into each art folder the first time it's created.</summary>
public static class ArtReadmes
{
    public const string Heroes = """
        Hero portraits go here.

        The easy way to fill this folder is Data -> Download Art..., which pulls
        them from the community asset API and names them correctly.

        To add one by hand, name the file after the hero_id column in
        data/heroes.csv, e.g.

            grey_talon.png
            mo_and_krill.png
            lady_geist.jpg

        Accepted extensions: .png .jpg .jpeg .webp .bmp
        Square images look best; anything else is cropped to a square, biased
        towards the top of the image so faces survive the crop.

        Any hero without a file here renders as a coloured tile with their
        initials, so the app works fine with this folder empty. After adding
        files, use View -> Reload Art (or just restart) to pick them up.

        """;

    public const string Items = """
        Item icons go here.

        The easy way to fill this folder is Data -> Download Art..., which pulls
        them from the community asset API and names them correctly.

        To add one by hand, name the file after the item_id column in
        data/items.csv, e.g.

            warp_stone.png
            bullet_lifesteal.png

        Accepted extensions: .png .jpg .jpeg .webp .bmp
        Square images look best; anything else is centre-cropped to a square.

        Any item without a file here renders as a tile tinted to its shop
        category, so the app works fine with this folder empty. After adding
        files, use View -> Reload Art (or just restart) to pick them up.

        """;

    public const string Topbar = """
        Reference art for screen detection.

        Data -> Download Art... fetches each hero's cards into _cards/ and cuts
        them where the top bar crops them, into the hero's folder:

            haze/card_normal.png       the portrait as usually drawn
            haze/state_critical.png    on critical health
            haze/state_gloat.png       on a kill streak

        <hero_id>.png is the API's own top-bar art, used only for a hero with no
        cut portraits.

        Correcting a detection in the review can keep that portrait too, as
        haze/variant_01.png and so on: that's how a skin gets recognised. A hero
        keeps their newest three; older ones move to _quarantine/, which detection
        ignores, and can be moved back.

        """;

    public static readonly IReadOnlyList<(string Folder, string Text)> All =
    [
        ("heroes", Heroes),
        ("items", Items),
        ("topbar", Topbar),
    ];
}
