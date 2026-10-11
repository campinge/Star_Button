using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScMapper.App.Controls;

/// <summary>Dialog windows get the same dark title bar as the main window.</summary>
public static class WindowChrome
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static Window DarkTitleBar(this Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, sizeof(int));
        };
        return w;
    }
}
