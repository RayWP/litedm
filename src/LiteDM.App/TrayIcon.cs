using System.Runtime.InteropServices;

namespace LiteDM.App;

internal sealed class TrayIcon : IDisposable
{
    private const int WmUser = 0x0400;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDblClk = 0x0203;
    private const int NidMessage = 1, NidIcon = 2, NidTip = 4, NidShowTip = 0x80;
    private const int NImAdd = 0, NImModify = 1, NImDelete = 2;
    private const int NimSetVersion = 4;
    private const uint NotifyVersion4 = 4;
    private const int NinSelect = 0x0400, NinKeySelect = 0x0401;
    private const uint CallbackMessage = 0x8000 + 47;
    private readonly nint _hwnd;
    private readonly Action _open;
    private readonly Func<Task> _exit;
    private readonly SubclassProc _proc;
    private readonly uint _id = (uint)Random.Shared.Next(1, ushort.MaxValue);
    private readonly uint _taskbarCreated;
    private bool _disposed;
    public bool Available { get; private set; }

    public TrayIcon(nint hwnd, Action open, Func<Task> exit)
    {
        _hwnd = hwnd; _open = open; _exit = exit;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _proc = SubclassWindowProc;
        Available = SetWindowSubclass(hwnd, _proc, _id, 0);
        var data = Data(); data.uFlags = NidMessage | NidIcon | NidTip | NidShowTip; data.szTip = "LiteDM - downloads continue";
        data.hIcon = LoadIcon(0, new nint(32512));
        if (Available) Available = AddIcon(ref data);
    }

    private nint SubclassWindowProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint subclassId, nuint refData)
    {
        if (msg == _taskbarCreated)
        {
            var data = Data(); data.uFlags = NidMessage | NidIcon | NidTip | NidShowTip; data.szTip = "LiteDM - downloads continue"; data.hIcon = LoadIcon(0, new nint(32512));
            Available = AddIcon(ref data);
            return 0;
        }
        if (msg == CallbackMessage)
        {
            var notification = unchecked((int)(lParam.ToInt64() & 0xffff));
            if (notification is WmLButtonUp or WmLButtonDblClk or NinSelect or NinKeySelect) _open();
            if (notification is WmContextMenu or 0x0205) ShowMenu();
            return 0;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, 1, "Open LiteDM");
        AppendMenu(menu, 0x0800, 0, null);
        AppendMenu(menu, 0, 2, "Exit");
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd);
        var selected = TrackPopupMenu(menu, 0x0100 | 0x0002 | 0x0001, point.X, point.Y, 0, _hwnd, 0);
        DestroyMenu(menu);
        if (selected == 1) _open();
        else if (selected == 2) _ = _exit();
    }

    private bool AddIcon(ref NOTIFYICONDATA data)
    {
        if (!Shell_NotifyIcon(NImAdd, ref data)) return false;
        data.uVersion = NotifyVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
        return true;
    }
    private NOTIFYICONDATA Data() => new() { cbSize = Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = _id, uCallbackMessage = CallbackMessage };
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data(); Shell_NotifyIcon(NImDelete, ref data);
        RemoveWindowSubclass(_hwnd, _proc, _id);
        GC.KeepAlive(_proc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize; public nint hWnd; public uint uID, uFlags, uCallbackMessage; public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? szTip;
        public uint dwState, dwStateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? szInfo;
        public uint uVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? szInfoTitle; public uint dwInfoFlags;
        public Guid guidItem; public nint hBalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nint wParam, nint lParam, nuint subclassId, nuint refData);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc callback, nuint id, nuint refData);
    [DllImport("comctl32.dll", SetLastError = true)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc callback, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")] private static extern bool AppendMenu(nint menu, uint flags, uint id, string? text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
}
