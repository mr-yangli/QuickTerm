namespace QuickTerm.App;

using System.Globalization;
using System.Resources;

/// <summary>
/// The UI text layer, sized for exactly two cultures today: English is the neutral (source) set and
/// Simplified Chinese is a satellite. Adding a third costs one <c>.resx</c> and one line in
/// <see cref="Available"/>, and nothing else — which is the whole reason strings never sit inline.
/// </summary>
/// <remarks>
/// The manager is built from <c>typeof(UiStrings)</c> rather than a hand-written base name: the manifest
/// resource name carries the root namespace, so a literal like <c>"Ui"</c> throws
/// <see cref="MissingManifestResourceException"/>. That was measured, not assumed.
/// Unknown cultures (a <c>zh-Hant</c> user, say) fall through to English. Pretending to translate what has
/// no resource file is worse than showing the source language.
/// </remarks>
public static class UiStrings
{
    /// <summary>The neutral, source-language culture.</summary>
    public const string NeutralTag = "en";

    /// <summary>Simplified Chinese — the only satellite today.</summary>
    public const string ChineseTag = "zh-Hans";

    private static readonly ResourceManager Manager = new(typeof(UiStrings));

    /// <summary>What the language picker offers; deliberately an explicit list, not runtime discovery.</summary>
    public static IReadOnlyList<string> Available { get; } = [NeutralTag, ChineseTag];

    /// <summary>English names for the two, so the picker can label each option in its own language.</summary>
    public static string DisplayName(string tag) => tag switch
    {
        ChineseTag => "简体中文",
        _ => "English",
    };

    /// <summary>System culture mapped onto the supported set: anything Chinese-ish takes the satellite, the rest is English.</summary>
    public static string DetectSystemTag()
    {
        var current = CultureInfo.CurrentUICulture;
        return current.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? ChineseTag : NeutralTag;
    }

    public static CultureInfo CultureFor(string tag)
        => tag == ChineseTag ? new CultureInfo(ChineseTag) : CultureInfo.InvariantCulture;

    /// <summary>The language the app is showing. One value on purpose: a per-tab language is not a promise this tool makes.</summary>
    public static string Current { get; private set; } = DetectSystemTag();

    /// <summary>Applies the choice to the whole thread and to the app's own reading of it.</summary>
    public static void Apply(string tag)
    {
        Current = Available.Contains(tag) ? tag : NeutralTag;
        var culture = CultureFor(Current);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string Get(string key) => Get(key, CultureFor(Current));

    public static string Get(string key, CultureInfo culture)
    {
        var text = Manager.GetString(key, culture);
        return string.IsNullOrEmpty(text) ? key : text;
    }

    /// <summary>Format-with-arguments path, so <c>{0}</c> placeholders stay out of the call site's code.</summary>
    public static string Format(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>The culture-explicit form: tests and any future per-surface language must not depend on the ambient one.</summary>
    public static string Format(string key, CultureInfo culture, params object[] args)
        => string.Format(Get(key, culture), args);

    public static bool Has(string key, CultureInfo culture) => !string.IsNullOrEmpty(Manager.GetString(key, culture));
}
