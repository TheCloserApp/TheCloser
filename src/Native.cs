using System;
using System.Runtime.InteropServices;

namespace TheCloser
{
    /// <summary>Win32 interop used by the overlay (capture exclusion, hotkeys, dark chrome).</summary>
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCHITTEST = 0x0084;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int WM_SETREDRAW = 0x000B;
        public const int WM_VSCROLL = 0x0115;
        public const int SB_LINEUP = 0, SB_LINEDOWN = 1, SB_PAGEUP = 2, SB_PAGEDOWN = 3;
        public const int EM_GETSCROLLPOS = 0x0400 + 221;
        public const int EM_SETSCROLLPOS = 0x0400 + 222;
        public const int EM_SETCUEBANNER = 0x1501;

        public const int HTCLIENT = 1, HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
            HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        public const uint WDA_NONE = 0x0;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        // Undocumented but stable since Windows 10 1903: lets classic scrollbars render dark.
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        private static extern int SetPreferredAppMode(int mode);

        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        public static void AllowDarkApp()
        {
            try { SetPreferredAppMode(1); } catch { }
        }

        public static void DarkScrollbars(IntPtr hwnd)
        {
            try { AllowDarkModeForWindow(hwnd, true); } catch { }
            try { SetWindowTheme(hwnd, "DarkMode_Explorer", null); } catch { }
        }

        /// <summary>Dark visual style for combo boxes (the theme Windows uses in dark file dialogs).</summary>
        public static void DarkCombo(IntPtr hwnd)
        {
            try { AllowDarkModeForWindow(hwnd, true); } catch { }
            try { SetWindowTheme(hwnd, "DarkMode_CFD", null); } catch { }
        }

        public static void DarkTitleBar(IntPtr hwnd)
        {
            int on = 1;
            try { DwmSetWindowAttribute(hwnd, 20, ref on, 4); } catch { }
        }

        /// <summary>Windows 11 rounded corners + a thin coloured border for borderless windows.</summary>
        public static void RoundedFrame(IntPtr hwnd, int borderColorBgr)
        {
            int round = 2; // DWMWCP_ROUND
            try { DwmSetWindowAttribute(hwnd, 33, ref round, 4); } catch { }
            try { DwmSetWindowAttribute(hwnd, 34, ref borderColorBgr, 4); } catch { }
        }

        public static void CaptionColor(IntPtr hwnd, int colorBgr)
        {
            try { DwmSetWindowAttribute(hwnd, 35, ref colorBgr, 4); } catch { }
        }

        /// <summary>Excludes a window from screenshots, recordings and screen sharing (Windows 10 2004+).</summary>
        public static bool SetCaptureExcluded(IntPtr hwnd, bool excluded)
        {
            return SetWindowDisplayAffinity(hwnd, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        }

        public static int ToBgr(System.Drawing.Color c)
        {
            return c.R | (c.G << 8) | (c.B << 16);
        }
    }
}
