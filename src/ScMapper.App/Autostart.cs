using Microsoft.Win32;

namespace ScMapper.App;

/// <summary>"Start with Windows" via the current user's Run key (no admin rights needed).</summary>
internal static class Autostart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StarButton";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(ValueName) != null;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (on && Environment.ProcessPath is { } exe) k.SetValue(ValueName, $"\"{exe}\" --minimized");
        else k.DeleteValue(ValueName, false);
    }
}
