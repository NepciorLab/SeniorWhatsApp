using System.Runtime.InteropServices;

namespace SeniorWhatsApp;

/// <summary>Dostępny obszar ekranu (bez paska zadań). Można go nadpisać do testów układu, np. dla ekranu 1920x1080.</summary>
public static class Display
{
    public static System.Windows.Rect? Override { get; set; }

    public static System.Windows.Rect WorkArea => Override ?? System.Windows.SystemParameters.WorkArea;

    /// <summary>Zmniejsza okno dialogowe, jeśli nie mieści się na ekranie.</summary>
    public static void FitToScreen(System.Windows.Window w)
    {
        var a = WorkArea;
        w.MinWidth = Math.Min(w.MinWidth, a.Width * 0.9);
        w.MinHeight = Math.Min(w.MinHeight, a.Height * 0.85);
        w.Width = Math.Min(w.Width, a.Width * 0.96);
        w.Height = Math.Min(w.Height, a.Height * 0.94);
    }
}

public static class Native
{
    public const int SW_RESTORE = 9, SW_MAXIMIZE = 3;
    public const uint WM_CLOSE = 0x0010;
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOMOVE = 2, SWP_NOSIZE = 1, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Okno nie zabiera fokusu i nie pojawia się na pasku zadań ani w Alt+Tab.</summary>
    public static void MakeNoActivateToolWindow(IntPtr h) =>
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

    public static void KeepOnTop(IntPtr h) =>
        SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Maksymalizuje okno (jeśli jeszcze nie jest zmaksymalizowane) i wysuwa je na wierzch.</summary>
    public static void Maximize(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (!IsZoomed(h)) ShowWindow(h, SW_MAXIMIZE);
        SetForegroundWindow(h);
    }

    public static bool IsMaximizedOrMinimized(IntPtr h) => IsZoomed(h) || IsIconic(h);

    public static void Restore(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        if (IsIconic(h)) ShowWindow(h, SW_RESTORE);
    }
}
