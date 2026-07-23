using Microsoft.Win32;

namespace AIUsage.Tray;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIUsageTray";

    private static string InstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageTray");
    private static string InstalledExe => Path.Combine(InstallDir, "AIUsageTray.exe");

    private static string ExpectedRunValue => $"\"{InstalledExe}\"";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            // Exact match on our quoted install path only — a foreign/malformed value counts as disabled.
            return key?.GetValue(ValueName) is string v &&
                   string.Equals(v, ExpectedRunValue, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false; // called at startup — must never crash
        }
    }

    public static string? Enable()
    {
        try
        {
            var current = Environment.ProcessPath!;
            if (!string.Equals(current, InstalledExe, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(current, InstalledExe, overwrite: true);
            }
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, ExpectedRunValue);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return "autostart registration failed (policy or file access)";
        }
    }

    public static string? Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            // Delete only if the value is exactly ours — never remove a foreign installation's entry.
            if (key?.GetValue(ValueName) is string v &&
                string.Equals(v, ExpectedRunValue, StringComparison.OrdinalIgnoreCase))
                key.DeleteValue(ValueName);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "autostart removal failed (policy)";
        }
    }
}
