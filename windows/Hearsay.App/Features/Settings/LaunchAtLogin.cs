using System.Security;
using Microsoft.Win32;

namespace Hearsay.App.Features.Settings;

/// <summary>
/// "Launch Hearsay at login" (PLAN.md section 8, 18.3). The Mac's
/// <c>SMAppService.mainApp</c> in mac/Hearsay/Features/Settings/SettingsView.swift
/// (<c>LaunchAtLoginSection</c>); on Windows a value in
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. The user owns
/// the state (Task Manager > Startup apps can disable it), so Settings reads
/// it back after every change and whenever the section appears.
/// </summary>
internal interface ILaunchAtLogin
{
    bool IsEnabled { get; }

    /// <summary>Writes or removes the Run value. Only the Settings toggle calls this.</summary>
    /// <exception cref="IOException">The registry refused the change.</exception>
    /// <exception cref="UnauthorizedAccessException">The registry refused the change.</exception>
    /// <exception cref="SecurityException">The registry refused the change.</exception>
    void SetEnabled(bool enabled);
}

/// <summary>
/// The Run-key value <c>Hearsay</c> = <c>"&lt;path of Hearsay.exe&gt;"</c>.
/// Nothing writes it at startup or in tests; debug runs use
/// <see cref="ScratchLaunchAtLogin"/>. An MSIX build (W7) will use a
/// <c>StartupTask</c> instead.
/// </summary>
internal sealed class RunKeyLaunchAtLogin : ILaunchAtLogin
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Hearsay";

    private static string Command => $"\"{Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Hearsay.exe")}\"";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (SecurityException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, Command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}

/// <summary>In memory only, for debug runs (AGENTS.md: never write the Run key).</summary>
internal sealed class ScratchLaunchAtLogin : ILaunchAtLogin
{
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}
