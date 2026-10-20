namespace SnapGlyph.App;

internal static class ForegroundTracker
{
    private static IntPtr _hook;
    private static IntPtr _self;
    private static NativeMethods.WinEventDelegate? _callback;
    private static IntPtr _lastExternal;

    public static IntPtr LastExternal => _lastExternal;

    public static void Initialize()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }
        _callback = OnForegroundChanged;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            IntPtr.Zero,
            _callback,
            0,
            0,
            NativeMethods.WineventOutofcontext);
        Remember(NativeMethods.GetForegroundWindow());
    }

    public static void Start(IntPtr self)
    {
        Initialize();
        _self = self;
        Remember(NativeMethods.GetForegroundWindow());
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
        _callback = null;
        _self = IntPtr.Zero;
    }

    private static void OnForegroundChanged(
        IntPtr eventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        Remember(NativeMethods.GetForegroundWindow());
    }

    private static void Remember(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero ||
            hwnd == _self ||
            NativeMethods.IsCurrentProcessWindow(hwnd))
        {
            return;
        }
        if (NativeMethods.IsWindowVisible(hwnd))
        {
            _lastExternal = hwnd;
        }
    }
}
