using System.Windows;
using System.Windows.Media;

namespace Recliner.Services;

/// <summary>
/// Applies a named theme by pushing SolidColorBrush resources into
/// Application.Current.Resources. Because MainWindow and all dialogs
/// use DynamicResource for every themed color, calling Apply() at any
/// time causes an instant live repaint — no window reload required.
/// </summary>
public static class ThemeService
{
    public const string Default     = "Default";
    public const string RoseGold    = "RoseGold";
    public const string CottonCandy = "CottonCandy";

    public static string DisplayName(string key) => key switch
    {
        RoseGold    => "Rose Gold Standard",
        CottonCandy => "Cotton Candy UI",
        _           => "Default Dark"
    };

    /// <summary>Apply theme to running app — instant visual update.</summary>
    public static void Apply(string theme)
    {
        var colors = theme switch
        {
            RoseGold    => _roseGold,
            CottonCandy => _cottonCandy,
            _           => _default
        };

        var res = Application.Current.Resources;
        foreach (var (key, hex) in colors)
            res[key] = Brush(hex);
    }

    // ── Palettes ──────────────────────────────────────────────────────────

    private static readonly (string, string)[] _default =
    [
        ("BgMain",            "#1a1a1a"),
        ("BgSurface",         "#242424"),
        ("BgSurface2",        "#2c2c2c"),
        ("BgHeader",          "#111111"),
        ("BgRow",             "#2a2a2a"),
        ("BgRowAlt",          "#252525"),
        ("BgSelected",        "#1a3a5c"),
        ("BgHover",           "#232f3e"),
        ("BgInput",           "#1e1e1e"),
        ("BgThumb",           "#404040"),
        ("GridLine",          "#2e2e2e"),
        ("BorderMid",         "#3a3a3a"),
        ("BorderDim",         "#2a2a2a"),
        ("Accent",            "#4a9eff"),
        ("AccentHov",         "#5aaeff"),
        ("AccentPress",       "#3a8eef"),
        ("AccentDisabled",    "#2a4a6a"),
        ("TxtPrimary",        "#e8e8e8"),
        ("TxtSecondary",      "#a0a0a0"),
        ("TxtDim",            "#606060"),
        ("TxtLabel",          "#7a9fc8"),
        ("SectionHeaderColor","#c0d8f2"),
        ("ColPurple",         "#c792ea"),
        ("ColOrange",         "#f0a060"),
        // Status / functional — constant across all themes
        ("ColGreen",          "#4ec9b0"),
        ("ColRed",            "#f47878"),
        ("ColYellow",         "#dcc68a"),
    ];

    // ── Rose Gold Standard ─────────────────────────────────────────────────
    // Deep-dark warm backgrounds, rose-gold accent, mauve selection.
    // Status greens/reds/yellows unchanged — they are functional.
    private static readonly (string, string)[] _roseGold =
    [
        ("BgMain",            "#1c1618"),
        ("BgSurface",         "#251e22"),
        ("BgSurface2",        "#2e2628"),
        ("BgHeader",          "#120d10"),
        ("BgRow",             "#2a2028"),
        ("BgRowAlt",          "#261e24"),
        ("BgSelected",        "#4a1835"),
        ("BgHover",           "#3a2030"),
        ("BgInput",           "#1e181c"),
        ("BgThumb",           "#503040"),
        ("GridLine",          "#302035"),
        ("BorderMid",         "#4a3040"),
        ("BorderDim",         "#302035"),
        ("Accent",            "#c8788a"),
        ("AccentHov",         "#d88898"),
        ("AccentPress",       "#b86878"),
        ("AccentDisabled",    "#4a2030"),
        ("TxtPrimary",        "#f0e8ec"),
        ("TxtSecondary",      "#b09898"),
        ("TxtDim",            "#706060"),
        ("TxtLabel",          "#c88898"),
        ("SectionHeaderColor","#f0c0cc"),
        ("ColPurple",         "#e8a0d8"),
        ("ColOrange",         "#f0a060"),
        // Functional status colors — same as default
        ("ColGreen",          "#4ec9b0"),
        ("ColRed",            "#f47878"),
        ("ColYellow",         "#dcc68a"),
    ];

    // ── Cotton Candy UI ────────────────────────────────────────────────────
    // Dark purple-black backgrounds, candy-pink accent, soft lavender text.
    // Represents and welcomes everyone — same functional status colors.
    private static readonly (string, string)[] _cottonCandy =
    [
        ("BgMain",            "#18161e"),
        ("BgSurface",         "#201c28"),
        ("BgSurface2",        "#282434"),
        ("BgHeader",          "#100e16"),
        ("BgRow",             "#241e30"),
        ("BgRowAlt",          "#201b2c"),
        ("BgSelected",        "#3c1858"),
        ("BgHover",           "#2c1a40"),
        ("BgInput",           "#1a1624"),
        ("BgThumb",           "#504060"),
        ("GridLine",          "#302850"),
        ("BorderMid",         "#4a3868"),
        ("BorderDim",         "#302850"),
        ("Accent",            "#e890c8"),
        ("AccentHov",         "#f0a0d8"),
        ("AccentPress",       "#d880b8"),
        ("AccentDisabled",    "#3a1858"),
        ("TxtPrimary",        "#f2eafc"),
        ("TxtSecondary",      "#b8a8d8"),
        ("TxtDim",            "#705880"),
        ("TxtLabel",          "#c8a0e8"),
        ("SectionHeaderColor","#f8c0ea"),
        ("ColPurple",         "#e0a8f8"),
        ("ColOrange",         "#f0a060"),
        // Functional status colors — same as default
        ("ColGreen",          "#4ec9b0"),
        ("ColRed",            "#f47878"),
        ("ColYellow",         "#dcc68a"),
    ];

    private static SolidColorBrush Brush(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));
}
