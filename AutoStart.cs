using System;
using Microsoft.Win32;

namespace Petglare;

/// <summary>Starts Petglare when the current user signs in to Windows.</summary>
internal static class AutoStart
{
    /// <summary>The per-user Run key; tests point this at a scratch key.</summary>
    internal static string RunKey { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Petglare";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>True when the entry exists and starts this executable.</summary>
    internal static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value
                && value.Equals(Command, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// After the Petglare folder was moved: when the start entry points at a Petglare.exe that no
    /// longer exists, points it here. Returns whether it changed.
    /// </summary>
    internal static bool Relocate()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string value) return false;
        var executable = value.Trim().Trim('"');
        if (value.Equals(Command, StringComparison.OrdinalIgnoreCase) || !HookSettings.IsMovedPetglare(executable)) return false;
        key.SetValue(ValueName, Command, RegistryValueKind.String);
        return true;
    }

    internal static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
