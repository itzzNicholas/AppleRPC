// SPDX-License-Identifier: AGPL-3.0-only
using AppleRPC.Logging;
using Microsoft.Win32;

namespace AppleRPC;

/// <summary>"Avvia con Windows": a value under HKCU\...\Run (no admin rights needed).</summary>
public sealed class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AppleRPC";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string v &&
                       string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not read the startup registry value.", ex);
                return false;
            }
        }
    }

    /// <returns>true on success.</returns>
    public bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info($"Start with Windows {(enabled ? "enabled" : "disabled")}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not update the startup registry value.", ex);
            return false;
        }
    }
}
