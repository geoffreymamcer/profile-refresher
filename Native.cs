using System.Runtime.InteropServices;
using System.Text;

namespace ProfileRefresher;

/// <summary>Minimal Win32 / Shell interop. No third-party dependencies.</summary>
internal static class Native
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public const uint GW_OWNER = 4;
    public const int SW_RESTORE = 9;
    public const byte VK_SHIFT = 0x10;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12;
    public const byte VK_F5 = 0x74;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    /// <summary>Brings a window to the front. Returns true only if it really is the foreground window.</summary>
    public static bool FocusWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (GetForegroundWindow() == hwnd) return true;

        if (!SetForegroundWindow(hwnd))
        {
            // Windows only lets the foreground app hand off focus; a key event unlocks that.
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
        }

        for (int i = 0; i < 10; i++)
        {
            if (GetForegroundWindow() == hwnd) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    public static void PressKey(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    public static string GetTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    // ---- Per-window AppUserModel properties (Chromium stores the profile here) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        private ushort r1, r2, r3;
        public IntPtr p;
        private IntPtr p2;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant pv);

    private static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    private static readonly Guid AppUserModelFmtId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PID_RelaunchCommand = 2;
    private const uint PID_RelaunchDisplayName = 4;
    private const uint PID_AppId = 5;
    private const ushort VT_LPWSTR = 31;

    /// <summary>Returns System.AppUserModel.ID and System.AppUserModel.RelaunchCommand for a window.</summary>
    public static (string? AppId, string? RelaunchCommand, string? DisplayName) GetAppUserModelInfo(IntPtr hwnd)
    {
        IPropertyStore? store = null;
        try
        {
            var iid = IID_IPropertyStore;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store is null) return (null, null, null);
            return (ReadString(store, PID_AppId), ReadString(store, PID_RelaunchCommand),
                ReadString(store, PID_RelaunchDisplayName));
        }
        catch
        {
            return (null, null, null);
        }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }
    }

    private static string? ReadString(IPropertyStore store, uint pid)
    {
        var key = new PropertyKey { fmtid = AppUserModelFmtId, pid = pid };
        if (store.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == VT_LPWSTR && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { PropVariantClear(ref pv); }
    }
}
