using System.Diagnostics;
using Hearsay.App.Interop;
using Hearsay.Core.Settings;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace Hearsay.App.Features.Help;

/// <summary>
/// The "Hearsay Help" window: one instance of shared/help/&lt;lang&gt;/Help.html
/// for the interface language (copied into the app's <c>help</c> folder at
/// build time), in WebView2 with JavaScript off. Anchor links stay in the
/// view, web and mail links open in the default browser, and
/// <c>hearsay://open/...</c> links switch the main window's tab.
/// The shared page serves both apps: its CSS shows the
/// <c>data-platform="windows"</c> passages and hides the Mac ones only when
/// <c>&lt;html&gt;</c> has class <c>windows</c>, which this window adds to every
/// document it loads (PLAN.md 18.3, Help row).
/// Mirrors mac/Hearsay/Features/Help/HelpView.swift (<c>HelpWindow</c>,
/// <c>HelpView</c>, <c>HelpWebView</c>).
/// </summary>
internal sealed partial class HelpWindow : Window
{
    public const int DefaultWidth = 760;
    public const int DefaultHeight = 640;

    /// <summary>The class on <c>&lt;html&gt;</c> that selects this platform's passages.</summary>
    public const string PlatformClass = "windows";

    /// <summary>
    /// Run by the host at DOMContentLoaded of every help document. With the
    /// page's own scripts off (<c>IsScriptEnabled</c> false), WebView2 still
    /// runs <c>ExecuteScriptAsync</c> but not
    /// <c>AddScriptToExecuteOnDocumentCreatedAsync</c> (measured with
    /// WebView2 154), so the class cannot be set before parsing; the view
    /// stays transparent until it is set, so the Mac passages never show.
    /// </summary>
    private const string MarkPlatformScript = """
        document.documentElement.classList.add("windows");
        document.documentElement.classList.contains("windows");
        """;

    /// <summary>For the UI snapshots: the class and how many passages of each platform are shown.</summary>
    private const string DescribePlatformScript = """
        (function () {
          var shown = function (platform) {
            return Array.prototype.filter.call(
              document.querySelectorAll('[data-platform="' + platform + '"]'),
              function (element) { return getComputedStyle(element).display !== "none"; }).length;
          };
          return "class \"" + document.documentElement.className + "\", shown mac " + shown("mac")
            + ", shown windows " + shown("windows");
        })();
        """;

    private readonly Action<HelpDestination> onOpen;
    private readonly WebView2? webView;
    private readonly TaskCompletionSource<bool> firstLoad = new();

    public HelpWindow(InterfaceLanguage language, Action<HelpDestination> onOpen)
    {
        this.onOpen = onOpen;
        Title = Strings.HelpTitle;
        HelpFile = ContentPath(language);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Hearsay.ico"));
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi == 0 ? 1.0 : dpi / 96.0;
        AppWindow.ResizeClient(new SizeInt32((int)(DefaultWidth * scale), (int)(DefaultHeight * scale)));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(480 * scale);
            presenter.PreferredMinimumHeight = (int)(360 * scale);
        }

        if (File.Exists(HelpFile))
        {
            // Transparent until the first page has its platform class.
            webView = new WebView2 { Opacity = 0 };
            Content = webView;
            _ = LoadAsync(webView, new Uri(HelpFile));
        }
        else
        {
            firstLoad.TrySetResult(false);
            Content = new Grid
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"],
                Children =
                {
                    new TextBlock
                    {
                        Text = Strings.HelpMissing("Help.html"),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                },
            };
        }
    }

    /// <summary>Full path of the page this window shows.</summary>
    public string HelpFile { get; }

    /// <summary>
    /// <c>help\&lt;code&gt;\Help.html</c> next to Hearsay.exe for the
    /// interface language (the Mac picks the <c>.lproj</c> the same way).
    /// </summary>
    public static string ContentPath(InterfaceLanguage language) =>
        Path.Combine(AppContext.BaseDirectory, "help", language.Code(), "Help.html");

    /// <summary>
    /// For the UI snapshots: shows <paramref name="fragment"/> (or the top)
    /// and writes the view as a PNG. WebView2 draws out of process, so it is
    /// captured with <c>CapturePreviewAsync</c>, not RenderTargetBitmap
    /// (the Mac's <c>takeSnapshot</c> for the same reason).
    /// </summary>
    public async Task<bool> CaptureAsync(string? fragment, string pngPath)
    {
        if (!await WaitForFirstLoadAsync().ConfigureAwait(true) || webView is null) return false;
        var core = webView.CoreWebView2;
        var target = new UriBuilder(new Uri(HelpFile)) { Fragment = fragment ?? "" }.Uri;
        // A jump inside the loaded page is a same-document navigation that
        // raises no NavigationCompleted, so load a blank page in between.
        if (!await NavigateAsync(core, "about:blank").ConfigureAwait(true)
            || !await NavigateAsync(core, target.AbsoluteUri).ConfigureAwait(true))
        {
            return false;
        }
        await Task.Delay(400).ConfigureAwait(true);
        var platform = await core.ExecuteScriptAsync(DescribePlatformScript);
        AppLog.Write($"help: {fragment ?? "top"}: {platform}");
        // A page that still shows Mac passages would be the wrong help.
        if (!platform.Contains("shown mac 0", StringComparison.Ordinal))
        {
            AppLog.Write("help: the Mac passages are visible; the platform class did not apply");
            return false;
        }
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var capture = core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream).AsTask();
        if (await Task.WhenAny(capture, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(true) != capture)
        {
            AppLog.Write("help: CapturePreviewAsync timed out");
            return false;
        }
        await capture.ConfigureAwait(true);
        var bytes = new byte[stream.Size];
        using (var reader = new Windows.Storage.Streams.DataReader(stream.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
        }
        await File.WriteAllBytesAsync(pngPath, bytes).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// For the UI snapshots: clicks <paramref name="link"/> as the page would
    /// (through the same navigation handler) and waits a moment.
    /// </summary>
    public async Task<bool> FollowLinkAsync(string link)
    {
        if (!await WaitForFirstLoadAsync().ConfigureAwait(true) || webView is null) return false;
        webView.CoreWebView2.Navigate(link);
        await Task.Delay(500).ConfigureAwait(true);
        return true;
    }

    /// <summary>Waits up to 30 s for WebView2 to start and show the page.</summary>
    private async Task<bool> WaitForFirstLoadAsync()
    {
        var finished = await Task.WhenAny(firstLoad.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(true);
        if (finished == firstLoad.Task) return firstLoad.Task.Result;
        AppLog.Write("help: WebView2 did not load the page within 30 s");
        return false;
    }

    private static async Task<bool> NavigateAsync(CoreWebView2 core, string url)
    {
        var loaded = new TaskCompletionSource<bool>();
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => loaded.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += Completed;
        core.Navigate(url);
        var finished = await Task.WhenAny(loaded.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(true);
        core.NavigationCompleted -= Completed;
        return finished == loaded.Task && loaded.Task.Result;
    }

    private async Task LoadAsync(WebView2 view, Uri page)
    {
        try
        {
            await view.EnsureCoreWebView2Async();
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or FileNotFoundException)
        {
            AppLog.Write($"help: WebView2 unavailable: {error.Message}");
            view.Opacity = 1;
            firstLoad.TrySetResult(false);
            return;
        }
        var core = view.CoreWebView2;
        AppLog.Write($"help: WebView2 {core.Environment.BrowserVersionString} ready");
        core.Settings.IsScriptEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += OnNewWindowRequested;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        core.DOMContentLoaded += OnDOMContentLoaded;
        void Completed(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            core.NavigationCompleted -= Completed;
            AppLog.Write($"help: page loaded, success {args.IsSuccess}, status {args.WebErrorStatus}");
            // An error page has no platform passages to hide.
            if (!args.IsSuccess) view.Opacity = 1;
            firstLoad.TrySetResult(args.IsSuccess);
        }
        core.NavigationCompleted += Completed;
        core.Navigate(page.AbsoluteUri);
    }

    /// <summary>Sets <see cref="PlatformClass"/> on each help document, then shows the view.</summary>
    private async void OnDOMContentLoaded(CoreWebView2 sender, CoreWebView2DOMContentLoadedEventArgs args)
    {
        if (!sender.Source.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var set = await sender.ExecuteScriptAsync(MarkPlatformScript);
            if (set != "true") AppLog.Write($"help: platform class \"{PlatformClass}\" not set ({set})");
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Write($"help: could not set the platform class: {error.Message}");
        }
        // Shown even if the class failed: the Mac text is better than a blank window.
        if (webView is not null) webView.Opacity = 1;
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var url))
        {
            args.Cancel = true;
            return;
        }
        args.Cancel = !Handle(url);
    }

    /// <summary>target="_blank" and window.open: handled like a click, never a new window.</summary>
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var url) && Handle(url))
        {
            sender.Navigate(url.AbsoluteUri);
        }
    }

    /// <summary>WebView2 would ask to hand an unknown scheme to Windows; Hearsay decides instead.</summary>
    private void OnLaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    {
        args.Cancel = true;
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var url)) Handle(url);
    }

    private static void OpenInBrowser(Uri link)
    {
        try
        {
            Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            AppLog.Write($"help: could not open {link}: {error.Message}");
        }
    }

    /// <summary>Acts on a link; true when the web view should load it.</summary>
    private bool Handle(Uri url)
    {
        var decision = HelpNavigation.Decide(url, HelpFile);
        switch (decision.Kind)
        {
            case HelpNavigationKind.Load:
                return true;
            // Acting inside a WebView2 event (focus moves to another window or
            // app) can deadlock with the browser process, so act afterwards.
            case HelpNavigationKind.OpenInBrowser when decision.Link is { } link:
                DispatcherQueue.TryEnqueue(() => OpenInBrowser(link));
                return false;
            case HelpNavigationKind.OpenInApp when decision.Destination is { } destination:
                DispatcherQueue.TryEnqueue(() => onOpen(destination));
                return false;
            default:
                return false;
        }
    }
}
