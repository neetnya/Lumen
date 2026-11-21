using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Lumen.Native
{
    internal static class Win32
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_LBUTTONUP = 0x0202;

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, nIndex)
                : new IntPtr(GetWindowLong32(hWnd, nIndex));
        }

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        {
            return IntPtr.Size == 8
                ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
                : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
        }

        public static int GetWindowLong(IntPtr hWnd, int nIndex)
        {
            return unchecked((int)GetWindowLongPtr(hWnd, nIndex).ToInt64());
        }

        public static int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong)
        {
            return unchecked((int)SetWindowLongPtr(hWnd, nIndex, new IntPtr(dwNewLong)).ToInt64());
        }

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public delegate IntPtr WndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint VK_MEDIA_NEXT_TRACK = 0xB0;
        public const uint VK_MEDIA_PREV_TRACK = 0xB1;
        public const uint VK_MEDIA_STOP = 0xB2;
        public const uint VK_MEDIA_PLAY_PAUSE = 0xB3;

        /// <summary>让窗口点击穿透（桌面歌词锁定态）。</summary>
        public static void SetClickThrough(IntPtr hwnd, bool enabled)
        {
            if (hwnd == IntPtr.Zero) return;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if (enabled) ex |= (long)(WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE);
            else ex &= ~(long)(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        public static void SetTopMost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        public static void SetToolWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        public static RECT GetWorkArea(IntPtr hwnd)
        {
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf<MONITORINFO>();
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.rcWork;

            // 没有窗口句柄时 MonitorFromWindow 的结果不可靠（会拿到整个屏幕而不是
            // 工作区，窗口就跑到任务栏下面了）。改用 SPI_GETWORKAREA 问主显示器工作区。
            var probe = FindWindow("Shell_TrayWnd", null);
            if (probe != IntPtr.Zero)
            {
                var m2 = MonitorFromWindow(probe, 2);
                var i2 = new MONITORINFO();
                i2.cbSize = Marshal.SizeOf<MONITORINFO>();
                if (m2 != IntPtr.Zero && GetMonitorInfo(m2, ref i2)) return i2.rcWork;
            }

            var work = new RECT();
            if (SystemParametersInfo(SPI_GETWORKAREA, 0, ref work, 0)) return work;

            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            return new RECT
            {
                left = 0,
                top = 0,
                right = w > 0 ? w : 1920,
                bottom = h > 0 ? h : 1080
            };
        }

        private const uint SPI_GETWORKAREA = 0x0030;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);

        public static RECT GetMonitorRect(IntPtr hwnd)
        {
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MONITORINFO();
            info.cbSize = Marshal.SizeOf<MONITORINFO>();
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info)) return info.rcMonitor;

            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            return new RECT
            {
                left = 0,
                top = 0,
                right = w > 0 ? w : 1920,
                bottom = h > 0 ? h : 1080
            };
        }

        // ------------------------------------------------------------------
        // 回收站删除（SHFileOperation + FO_DELETE + FOF_ALLOWUNDO）
        // ------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public int fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        private const uint FO_DELETE = 0x0003;
        private const ushort FOF_ALLOWUNDO = 0x0040;      // 送进回收站
        private const ushort FOF_NOCONFIRMATION = 0x0010; // 不弹确认
        private const ushort FOF_SILENT = 0x0004;
        private const ushort FOF_NOCONFIRMMKDIR = 0x0200;
        private const ushort FOF_NOERRORUI = 0x0400;      // 禁止弹出错误对话框

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

        /// <summary>把文件送进回收站（不弹确认框，可恢复）。返回 0 表示成功。</summary>
        public static int DeleteToRecycleBin(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return -1;
            var fullPath = Path.GetFullPath(path);

            // SHFileOperation 要求 pFrom 以两个 null 结尾
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = fullPath + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOCONFIRMMKDIR | FOF_NOERRORUI
            };
            return SHFileOperation(ref op);
        }
    }
}
