using Hearsay.App.Features.Main;
using Hearsay.App.Features.Settings;

namespace Hearsay.App.Features.Help;

/// <summary>The <c>&lt;destination&gt;</c> of <c>hearsay://open/&lt;destination&gt;</c>.</summary>
internal enum HelpDestination
{
    Record,
    File,
    Models,
    History,
    Settings,
    SettingsGeneral,
    SettingsWindow,
    SettingsOutput,
    SettingsAI,
}

/// <summary>What the help window does with a link.</summary>
internal enum HelpNavigationKind
{
    /// <summary>The help page itself, including <c>#anchor</c> jumps inside it.</summary>
    Load,
    /// <summary>A web or mail link, opened in the default browser.</summary>
    OpenInBrowser,
    /// <summary><c>hearsay://open/&lt;destination&gt;</c>: a main-window tab.</summary>
    OpenInApp,
    /// <summary>Anything else is ignored.</summary>
    Ignore,
}

internal readonly record struct HelpNavigation(HelpNavigationKind Kind, Uri? Link = null, HelpDestination? Destination = null)
{
    public static HelpNavigation Load { get; } = new(HelpNavigationKind.Load);
    public static HelpNavigation Ignore { get; } = new(HelpNavigationKind.Ignore);

    /// <summary>
    /// Port of <c>HelpNavigation.decide(_:helpFile:)</c> in
    /// mac/Hearsay/Features/Help/HelpView.swift. File paths compare ignoring
    /// case, as NTFS does.
    /// </summary>
    public static HelpNavigation Decide(Uri url, string helpFile)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrEmpty(helpFile);
        if (!url.IsAbsoluteUri) return Ignore;
        switch (url.Scheme.ToLowerInvariant())
        {
            case "file":
                // Only the help file itself (with or without a fragment).
                return SamePath(url.LocalPath, helpFile) ? Load : Ignore;
            case "about":
                return url.OriginalString == "about:blank" ? Load : Ignore;
            case "https" or "http" or "mailto":
                return new HelpNavigation(HelpNavigationKind.OpenInBrowser, Link: url);
            case "hearsay":
                if (!string.Equals(url.Host, "open", StringComparison.OrdinalIgnoreCase)) return Ignore;
                var name = url.AbsolutePath.Trim('/').ToLowerInvariant();
                return DestinationFromName(name) is { } destination
                    ? new HelpNavigation(HelpNavigationKind.OpenInApp, Destination: destination)
                    : Ignore;
            default:
                return Ignore;
        }
    }

    public static HelpDestination? DestinationFromName(string name) => name switch
    {
        "record" => HelpDestination.Record,
        "file" => HelpDestination.File,
        "models" => HelpDestination.Models,
        "history" => HelpDestination.History,
        "settings" => HelpDestination.Settings,
        "settings-general" => HelpDestination.SettingsGeneral,
        "settings-window" => HelpDestination.SettingsWindow,
        "settings-output" => HelpDestination.SettingsOutput,
        "settings-ai" => HelpDestination.SettingsAI,
        _ => null,
    };

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (PathTooLongException)
        {
            return false;
        }
    }
}

internal static class HelpDestinations
{
    public static MainTab Tab(this HelpDestination destination) => destination switch
    {
        HelpDestination.Record => MainTab.Record,
        HelpDestination.File => MainTab.File,
        HelpDestination.Models => MainTab.Models,
        HelpDestination.History => MainTab.History,
        _ => MainTab.Settings,
    };

    /// <summary>The Settings section to show, null to leave it as it is.</summary>
    public static SettingsPane? SettingsPane(this HelpDestination destination) => destination switch
    {
        HelpDestination.SettingsGeneral => Settings.SettingsPane.General,
        HelpDestination.SettingsWindow => Settings.SettingsPane.Window,
        HelpDestination.SettingsOutput => Settings.SettingsPane.Output,
        HelpDestination.SettingsAI => Settings.SettingsPane.AI,
        _ => null,
    };
}
