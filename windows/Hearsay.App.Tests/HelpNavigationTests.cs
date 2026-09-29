using Hearsay.App.Features.Help;
using Hearsay.App.Features.Main;
using Hearsay.App.Features.Settings;

namespace Hearsay.App.Tests;

/// <summary>
/// <see cref="HelpNavigation.Decide"/>, the port of <c>HelpNavigation.decide</c>
/// in mac/Hearsay/Features/Help/HelpView.swift: the help page loads, web and
/// mail links go to the browser, <c>hearsay://open/&lt;tab&gt;</c> opens a tab,
/// everything else is ignored.
/// </summary>
public sealed class HelpNavigationTests
{
    private const string HelpFile = @"C:\Program Files\Hearsay\help\de\Help.html";

    private static HelpNavigation Decide(string url) => HelpNavigation.Decide(new Uri(url, UriKind.RelativeOrAbsolute), HelpFile);

    [Fact]
    public void TheHelpFileLoadsWithOrWithoutAnchor()
    {
        Assert.Equal(HelpNavigation.Load, Decide("file:///C:/Program%20Files/Hearsay/help/de/Help.html"));
        Assert.Equal(HelpNavigation.Load, Decide("file:///C:/Program%20Files/Hearsay/help/de/Help.html#models"));
        // NTFS compares names ignoring case.
        Assert.Equal(HelpNavigation.Load, Decide("file:///c:/program%20files/hearsay/HELP/DE/help.html"));
        Assert.Equal(HelpNavigation.Load, Decide("about:blank"));
    }

    [Fact]
    public void OtherFilesAndPagesAreIgnored()
    {
        Assert.Equal(HelpNavigation.Ignore, Decide("file:///C:/Program%20Files/Hearsay/help/en/Help.html"));
        Assert.Equal(HelpNavigation.Ignore, Decide("file:///C:/Windows/win.ini"));
        Assert.Equal(HelpNavigation.Ignore, Decide("about:srcdoc"));
        Assert.Equal(HelpNavigation.Ignore, Decide("javascript:alert(1)"));
        Assert.Equal(HelpNavigation.Ignore, Decide("ftp://example.com/x"));
        Assert.Equal(HelpNavigation.Ignore, Decide("Help.html#models"));
    }

    [Theory]
    [InlineData("https://github.com/og1o/hearsay")]
    [InlineData("http://localhost:11434")]
    [InlineData("mailto:chihlingw@gmail.com")]
    [InlineData("HTTPS://example.com/")]
    public void WebAndMailLinksOpenInTheBrowser(string url)
    {
        var decision = Decide(url);
        Assert.Equal(HelpNavigationKind.OpenInBrowser, decision.Kind);
        Assert.Equal(new Uri(url), decision.Link);
        Assert.Null(decision.Destination);
    }

    [Theory]
    [InlineData("hearsay://open/record", nameof(HelpDestination.Record))]
    [InlineData("hearsay://open/file", nameof(HelpDestination.File))]
    [InlineData("hearsay://open/models", nameof(HelpDestination.Models))]
    [InlineData("hearsay://open/history", nameof(HelpDestination.History))]
    [InlineData("hearsay://open/settings", nameof(HelpDestination.Settings))]
    [InlineData("hearsay://open/settings-general", nameof(HelpDestination.SettingsGeneral))]
    [InlineData("hearsay://open/settings-window", nameof(HelpDestination.SettingsWindow))]
    [InlineData("hearsay://open/settings-output", nameof(HelpDestination.SettingsOutput))]
    [InlineData("hearsay://open/settings-ai", nameof(HelpDestination.SettingsAI))]
    [InlineData("hearsay://open/Settings-AI/", nameof(HelpDestination.SettingsAI))]
    [InlineData("HEARSAY://OPEN/Models", nameof(HelpDestination.Models))]
    public void AppLinksOpenATab(string url, string destinationName)
    {
        var destination = Enum.Parse<HelpDestination>(destinationName);
        Assert.Equal(new HelpNavigation(HelpNavigationKind.OpenInApp, Destination: destination), Decide(url));
    }

    [Theory]
    [InlineData("hearsay://open/unknown")]
    [InlineData("hearsay://open/")]
    [InlineData("hearsay://show/models")]
    [InlineData("hearsay://open/settings/ai")]
    public void UnknownAppLinksAreIgnored(string url)
    {
        Assert.Equal(HelpNavigation.Ignore, Decide(url));
    }

    [Fact]
    public void DestinationsMapToTabsAndSettingsPanes()
    {
        Assert.Equal(MainTab.Record, HelpDestination.Record.Tab());
        Assert.Equal(MainTab.File, HelpDestination.File.Tab());
        Assert.Equal(MainTab.Models, HelpDestination.Models.Tab());
        Assert.Equal(MainTab.History, HelpDestination.History.Tab());
        Assert.Null(HelpDestination.Settings.SettingsPane());
        Assert.Null(HelpDestination.Models.SettingsPane());
        foreach (var (destination, pane) in new[]
        {
            (HelpDestination.SettingsGeneral, SettingsPane.General),
            (HelpDestination.SettingsWindow, SettingsPane.Window),
            (HelpDestination.SettingsOutput, SettingsPane.Output),
            (HelpDestination.SettingsAI, SettingsPane.AI),
        })
        {
            Assert.Equal(MainTab.Settings, destination.Tab());
            Assert.Equal(pane, destination.SettingsPane());
        }
        Assert.Equal(MainTab.Settings, HelpDestination.Settings.Tab());
    }
}
