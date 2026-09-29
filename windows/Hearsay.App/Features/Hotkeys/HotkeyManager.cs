using System.ComponentModel;
using System.Runtime.InteropServices;
using Hearsay.App.Interop;
using Hearsay.Core.Settings;

namespace Hearsay.App.Features.Hotkeys;

/// <summary>A global shortcut's command.</summary>
internal enum HotkeyAction
{
    StartStop = 1,
    Pause = 2,
}

/// <summary>
/// Global hotkeys for Start / Stop and Pause / Resume (PLAN.md 4.4, 18.3).
/// Port of mac/Hearsay/Features/Hotkeys/HotkeyManager.swift: Win32
/// <c>RegisterHotKey</c> in place of Carbon <c>RegisterEventHotKey</c>, with
/// <see cref="HotkeyBinding"/>'s values (MOD_* flags plus MOD_NOREPEAT, VK
/// codes), targeting a message-only window on the UI thread. Bindings come
/// from <see cref="AppSettings"/> and are re-registered whenever either one
/// changes; <see cref="Dispose"/> unregisters them (on quit). Neither needs
/// any permission. Use from the UI thread.
/// </summary>
internal sealed class HotkeyManager : INotifyPropertyChanged, IDisposable
{
    private readonly AppSettings settings;
    private readonly Action<HotkeyAction> perform;
    private readonly HashSet<HotkeyAction> registered = [];
    private MessageWindow? window;
    private bool suspended;
    private string? registrationError;

    public HotkeyManager(AppSettings settings, Action<HotkeyAction> perform)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(perform);
        this.settings = settings;
        this.perform = perform;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Shortcuts that could not be registered, for example because another
    /// app already owns the combination. Shown in Settings.
    /// </summary>
    public string? RegistrationError
    {
        get => registrationError;
        private set
        {
            if (registrationError == value) return;
            registrationError = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RegistrationError)));
        }
    }

    /// <summary>Creates the message window, registers the current bindings, and watches the settings.</summary>
    public void Start()
    {
        if (window is not null) return;
        try
        {
            window = new MessageWindow();
        }
        catch (Win32Exception error)
        {
            RegistrationError = Strings.ShortcutsUnavailable(error.NativeErrorCode);
            return;
        }
        window.MessageReceived += OnMessage;
        settings.PropertyChanged += OnSettingsChanged;
        Register();
    }

    /// <summary>Unregisters the hotkeys while a shortcut recorder listens.</summary>
    public void Suspend()
    {
        suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        suspended = false;
        Register();
    }

    public void Dispose()
    {
        settings.PropertyChanged -= OnSettingsChanged;
        UnregisterAll();
        window?.Dispose();
        window = null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.StartStopHotkey) or nameof(AppSettings.PauseHotkey))
        {
            Register();
        }
    }

    private void Register()
    {
        UnregisterAll();
        if (suspended || window is null) return;
        var failures = new List<string>();
        (HotkeyAction Action, HotkeyBinding Binding)[] bindings =
        [
            (HotkeyAction.StartStop, settings.StartStopHotkey),
            (HotkeyAction.Pause, settings.PauseHotkey),
        ];
        foreach (var (action, binding) in bindings)
        {
            if (NativeMethods.RegisterHotKey(window.Handle, (int)action, binding.RegisterHotKeyModifiers, binding.VirtualKey))
            {
                registered.Add(action);
                AppLog.Write($"hotkeys: registered {action} as {binding.DisplayString}");
            }
            else
            {
                var error = Marshal.GetLastWin32Error();
                AppLog.Write($"hotkeys: {binding.DisplayString} for {action} failed, error {error}");
                failures.Add(Strings.ShortcutTaken(binding.DisplayString));
            }
        }
        RegistrationError = failures.Count == 0 ? null : string.Join('\n', failures);
    }

    private void UnregisterAll()
    {
        if (window is null) return;
        foreach (var action in registered)
        {
            NativeMethods.UnregisterHotKey(window.Handle, (int)action);
        }
        registered.Clear();
    }

    private bool OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message != NativeMethods.WM_HOTKEY) return false;
        var id = (int)wParam.ToInt64();
        if (id is not ((int)HotkeyAction.StartStop or (int)HotkeyAction.Pause)) return false;
        var action = (HotkeyAction)id;
        AppLog.Write($"hotkeys: {action} pressed");
        perform(action);
        return true;
    }
}
