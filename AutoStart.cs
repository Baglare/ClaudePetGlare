using System;
using Microsoft.Win32;

namespace Petglare;

/// <summary>Starts Petglare when the current user signs in to Windows.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
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

    internal static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
