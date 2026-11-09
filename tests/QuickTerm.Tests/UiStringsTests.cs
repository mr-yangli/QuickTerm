namespace QuickTerm.Tests;

using System.Globalization;
using QuickTerm.App;

/// <summary>
/// The text layer's own contract: every key answers in both cultures, the fallback is English rather
/// than an exception, and nothing here depends on a hand-written manifest name. The first version of this
/// class used <c>new ResourceManager("QuickTerm.Ui")</c> and threw — the manifest name carries the root
/// namespace — which is exactly the bug these tests exist to keep from coming back.
/// </summary>
public sealed class UiStringsTests
{
    private static readonly string[] Keys =
    [
        "AppTitle", "Settings", "Font", "FontSize", "Theme", "Scrollback", "Language", "FollowSystem",
        "Copy", "Paste", "NewTerminal", "Reconnect", "Disconnected", "ExitedWithCode", "FpsOverlay",
        "ScrollbackLabel", "FrameDamageLabel", "PtyStartFailed",
    ];

    private static readonly CultureInfo Chinese = new("zh-Hans");

    [Fact]
    public void Every_key_answers_in_the_neutral_set()
    {
        foreach (var key in Keys)
            Assert.False(string.IsNullOrEmpty(UiStrings.Get(key, CultureInfo.InvariantCulture)), $"{key} missing in en");
    }

    [Fact]
    public void Every_key_answers_in_the_satellite()
    {
        foreach (var key in Keys)
            Assert.False(string.IsNullOrEmpty(UiStrings.Get(key, Chinese)), $"{key} missing in zh-Hans");
    }

    [Fact]
    public void The_satellite_actually_differs_from_the_source_language()
    {
        Assert.Equal("Settings", UiStrings.Get("Settings", CultureInfo.InvariantCulture));
        Assert.Equal("设置", UiStrings.Get("Settings", Chinese));
    }

    [Fact]
    public void A_culture_with_no_resource_falls_back_to_english_instead_of_blanking_out()
    {
        Assert.Equal("Settings", UiStrings.Get("Settings", new CultureInfo("zh-Hant")));
        Assert.Equal("Settings", UiStrings.Get("Settings", new CultureInfo("de-DE")));
    }

    [Fact]
    public void Placeholders_survive_the_round_trip_in_both_languages()
    {
        // Culture-explicit on purpose: the machine this runs on is itself a Simplified-Chinese desktop,
        // so an assertion that leaned on the ambient culture would read Chinese and call it a failure.
        Assert.Equal("Process exited with code 7", UiStrings.Format("ExitedWithCode", CultureInfo.InvariantCulture, 7));
        Assert.Equal("进程已退出，代码 7", UiStrings.Format("ExitedWithCode", Chinese, 7));
    }

    [Fact]
    public void The_language_the_app_is_showing_moves_the_reading_with_it()
    {
        var before = UiStrings.Current;
        try
        {
            UiStrings.Apply(UiStrings.ChineseTag);
            Assert.Equal(UiStrings.ChineseTag, UiStrings.Current);
            Assert.Contains("代码", UiStrings.Format("ExitedWithCode", 7));
        }
        finally
        {
            UiStrings.Apply(before);
        }
    }

    [Fact]
    public void A_tag_that_has_no_resource_is_refused_rather_than_falling_through_blank()
    {
        var before = UiStrings.Current;
        try
        {
            UiStrings.Apply("pt-BR");
            Assert.Equal(UiStrings.NeutralTag, UiStrings.Current);
        }
        finally
        {
            UiStrings.Apply(before);
        }
    }

    [Fact]
    public void An_unknown_key_shows_the_key_rather_than_throwing()
    {
        Assert.Equal("NoSuchKey", UiStrings.Get("NoSuchKey", Chinese));
        Assert.False(UiStrings.Has("NoSuchKey", Chinese));
    }

    [Fact]
    public void The_promise_is_two_languages_and_the_picker_offers_exactly_those()
    {
        Assert.Equal(2, UiStrings.Available.Count);
        Assert.Equal(UiStrings.NeutralTag, UiStrings.Available[0]);
        Assert.Equal(UiStrings.ChineseTag, UiStrings.Available[1]);
    }

    [Fact]
    public void Pickers_label_each_option_in_its_own_language()
    {
        Assert.Equal("English", UiStrings.DisplayName(UiStrings.NeutralTag));
        Assert.Equal("简体中文", UiStrings.DisplayName(UiStrings.ChineseTag));
    }
}
