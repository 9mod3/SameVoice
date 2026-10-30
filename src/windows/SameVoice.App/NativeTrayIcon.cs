using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SameVoice.App;

/// <summary>
/// Small native Shell_NotifyIcon wrapper. WinForms NotifyIcon hides the return
/// value of Shell_NotifyIcon, which makes a missing tray icon impossible to
/// diagnose. This wrapper logs the actual HRESULT and re-adds itself after an
/// Explorer restart.
/// </summary>
internal sealed class NativeTrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 21;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIF_GUID = 0x00000020;
    private const uint NIF_SHOWTIP = 0x00000080;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;

    private const uint NIIF_INFO = 0x00000001;

    private static readonly Guid IconGuid = new("6D3A9C2E-4B87-4F5D-9A10-8E1C2B7D4A63");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    private readonly Action<string> _log;
    private readonly Action _open;
    private readonly Action _menu;
    private readonly Icon _icon;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMessage;
    private NOTIFYICONDATA _data;
    private bool _useGuid;
    private bool _added;

    public bool IsAdded => _added;
    public int LastError { get; private set; }

    public NativeTrayIcon(Icon icon, Action<string> log, Action open, Action menu)
    {
        _icon = icon;
        _log = log;
        _open = open;
        _menu = menu;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var parameters = new HwndSourceParameters("SameVoiceTraySink")
        {
            // A hidden top-level window is more compatible with the shell than a
            // message-only window on some Windows 11 builds. It is never shown.
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon.Handle,
            szTip = "同声 SameVoice",
            szInfo = "",
            szInfoTitle = "",
            guidItem = IconGuid
        };
        _useGuid = true;

        if (!TryAdd())
        {
            _log("NIM_ADD failed with GUID and without GUID");
            return;
        }

        var version = _data;
        version.uVersion = NOTIFYICON_VERSION_4;
        if (!Shell_NotifyIcon(NIM_SETVERSION, ref version))
            _log("NIM_SETVERSION failed: " + Marshal.GetLastWin32Error());

        LogIconRect();
    }

    private bool TryAdd()
    {
        bool ok = Shell_NotifyIcon(NIM_ADD, ref _data);
        int error = Marshal.GetLastWin32Error();
        _log($"NIM_ADD guid={_useGuid} ok={ok} error={error}");

        if (!ok && _useGuid)
        {
            _useGuid = false;
            _data.uFlags &= ~NIF_GUID;
            _data.guidItem = Guid.Empty;
            ok = Shell_NotifyIcon(NIM_ADD, ref _data);
            error = Marshal.GetLastWin32Error();
            _log($"NIM_ADD fallback ok={ok} error={error}");
        }

        _added = ok;
        LastError = ok ? 0 : error;
        return ok;
    }

    private void LogIconRect()
    {
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _data.hWnd,
            uID = _data.uID,
            guidItem = _useGuid ? IconGuid : Guid.Empty
        };
        int hr = Shell_NotifyIconGetRect(ref id, out RECT rect);
        _log(hr == 0
            ? $"GetRect ok left={rect.Left} top={rect.Top} right={rect.Right} bottom={rect.Bottom}"
            : $"GetRect failed hr=0x{hr:X8}");
    }

    public void SetTooltip(string text)
    {
        if (!_added) return;
        var data = _data;
        data.uFlags = NIF_TIP;
        data.szTip = text.Length > 127 ? text[..127] : text;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        var data = _data;
        data.uFlags = NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = message;
        data.dwInfoFlags = NIIF_INFO;
        data.uVersion = 5000;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int evt = (int)(lParam.ToInt64() & 0xFFFF);
            if (evt == WM_LBUTTONDBLCLK || evt == NIN_SELECT)
            {
                _open();
                handled = true;
            }
            else if (evt == WM_RBUTTONUP || evt == WM_CONTEXTMENU)
            {
                _menu();
                handled = true;
            }
        }
        else if (msg == (int)_taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted. The old shell icon is gone; add it again.
            _added = false;
            _data.hWnd = hwnd;
            if (TryAdd())
            {
                var version = _data;
                version.uVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIcon(NIM_SETVERSION, ref version);
                LogIconRect();
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _data);
            _added = false;
        }
        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch
        {
            // ignore
        }
        try { _icon.Dispose(); } catch { /* ignore */ }
    }
}
