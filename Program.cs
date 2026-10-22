using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace HdrToggle;

// Tray-only HDR toggle.
internal static unsafe class Program
{
    const uint WM_APP_TRAY = 0x8001, WM_APP_TOGGLE = 0x8002, WM_APP_CAPTURED = 0x8003, WM_APP_DONE = 0x8004;
    const uint MOD_ALT = 1, MOD_CTRL = 2, MOD_SHIFT = 4, MOD_WIN = 8, MOD_NOREPEAT = 0x4000;
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;
    const string AppKey = @"Software\HdrToggle";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    static nint _hwnd, _hook;
    static uint _mods = MOD_CTRL | MOD_ALT, _vk = 'H', _taskbarCreated, _swallowUp;
    static bool _useHook, _capturing, _held, _busy, _notify, _hdrOn, _hdrAvail, _firstRun, _light;
    static int _iconSize;
    static long _lastRefresh;
    static nint[] _icons = new nint[4];
    static readonly string[] IconNames = { "tray-dark-on.ico", "tray-dark-off.ico", "tray-light-on.ico", "tray-light-off.ico" };

    static void Main()
    {
        using var mutex = new Mutex(true, "HdrToggle.SingleInstance", out bool first);
        if (!first) return;

        LoadSettings();
        HealAutostart();
        try { SetPreferredAppMode(1); FlushMenuThemes(); } catch { }   // dark/light native menus
        _taskbarCreated = RegMsg("TaskbarCreated");

        fixed (char* cls = "HdrToggleWindow")
        {
            nint inst = GetModuleHandleW(null);
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&WndProc,
                hInstance = inst,
                lpszClassName = cls,
            };
            RegisterClassExW(&wc);
            _hwnd = CreateWindowExW(0, cls, cls, 0, 0, 0, 0, 0, 0, 0, inst, 0);
        }
        if (_hwnd == 0) return;

        Hdr.GetState(out _hdrAvail, out _hdrOn);
        _light = IsLightTaskbar();
        _iconSize = GetSystemMetrics(49);
        Tray(NIM_ADD);

        if (!Bind(_mods, _vk))
            Balloon(Format(_mods, _vk) + " is already in use. Right-click the tray icon to choose another shortcut.");
        else if (_firstRun)
            Balloon("Running. Shortcut: " + Format(_mods, _vk) + ". Right-click the icon for options.");

        MSG msg = default;
        while (GetMessageW(&msg, 0, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }

        Tray(NIM_DELETE);
        UnregisterHotKey(_hwnd, 1);
        if (_hook != 0) UnhookWindowsHookEx(_hook);
        foreach (var h in _icons) if (h != 0) DestroyIcon(h);
    }

    // ------------------------------------------------------------------ window procedure

    [UnmanagedCallersOnly]
    static nint WndProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        try
        {
            if (msg != 0 && msg == _taskbarCreated) { Tray(NIM_ADD); return 0; }   // Explorer restarted

            switch (msg)
            {
                case WM_APP_TRAY:
                    switch ((uint)lParam)
                    {
                        case 0x202: Toggle(); break;      // left click
                        case 0x205: ShowMenu(); break;    // right click
                        case 0x200:                       // hover: pick up HDR changes made elsewhere
                            if (Environment.TickCount64 - _lastRefresh > 1000) { _lastRefresh = Environment.TickCount64; Refresh(); }
                            break;
                    }
                    return 0;

                case 0x312: Toggle(); return 0;           // WM_HOTKEY

                case WM_APP_TOGGLE:                       // hotkey handled by the keyboard hook
                    if (wParam != 0) SendDummyKey();
                    Toggle();
                    return 0;

                case WM_APP_DONE:
                {
                    _busy = false;
                    int r = (int)wParam - 1;
                    if (r < 0) Balloon("Couldn't switch HDR. No HDR-capable display found.");
                    else
                    {
                        _hdrOn = r == 1; _hdrAvail = true; Tray(NIM_MODIFY);
                        if (_notify) Balloon(_hdrOn ? "HDR is on" : "HDR is off");
                    }
                    return 0;
                }

                case WM_APP_CAPTURED:
                {
                    if (!_capturing) return 0;
                    EndCapture();
                    uint m = (uint)wParam, vk = (uint)lParam;
                    if (vk == 0) { Bind(_mods, _vk); Balloon("Cancelled."); }
                    else
                    {
                        if ((m & (MOD_ALT | MOD_WIN)) != 0) SendDummyKey();
                        SetHotkey(m, vk);
                    }
                    return 0;
                }

                case 0x113:                               // WM_TIMER: capture timed out
                    if (wParam == 1 && _capturing) { EndCapture(); Bind(_mods, _vk); Balloon("Cancelled."); }
                    return 0;

                case 0x1A: OnSettingChange(); break;      // theme / DPI changes
                case 0x7E: Refresh(); break;              // display change
                case 2: PostQuitMessage(0); return 0;
            }
        }
        catch { }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ------------------------------------------------------------------ toggle

    static void Toggle()
    {
        if (_busy) return;
        _busy = true;
        // Switching can take a moment; keep the message loop (and the keyboard hook) responsive.
        ThreadPool.QueueUserWorkItem(static _ => PostMessageW(_hwnd, WM_APP_DONE, (nuint)(Hdr.Toggle() + 1), 0));
    }

    static void Refresh()
    {
        Hdr.GetState(out bool avail, out bool on);
        bool changed = avail != _hdrAvail || on != _hdrOn;
        _hdrAvail = avail; _hdrOn = on;
        if (changed) Tray(NIM_MODIFY);
    }

    // ------------------------------------------------------------------ hotkey

    /// <summary>Registers the combo. Win-combos the shell reserves (e.g. Win+H) fall back to a keyboard hook.</summary>
    static bool Bind(uint mods, uint vk)
    {
        UnregisterHotKey(_hwnd, 1);
        _useHook = false;
        bool ok = RegisterHotKey(_hwnd, 1, mods | MOD_NOREPEAT, vk) != 0;
        if (!ok && (mods & MOD_WIN) != 0)
        {
            _useHook = true;
            SyncHook();
            ok = _hook != 0;
            _useHook = ok;
        }
        if (ok) { _mods = mods; _vk = vk; }
        SyncHook();
        return ok;
    }

    static void SetHotkey(uint mods, uint vk)
    {
        uint pm = _mods, pv = _vk;
        if (Bind(mods, vk)) { SaveSettings(); Balloon("Shortcut set to " + Format(mods, vk)); }
        else { Bind(pm, pv); Balloon("That shortcut is already used by Windows or another app."); }
    }

    static void SyncHook()
    {
        bool need = _useHook || _capturing;
        if (need && _hook == 0)
            _hook = SetWindowsHookExW(13, (nint)(delegate* unmanaged<int, nuint, nint, nint>)&HookProc, GetModuleHandleW(null), 0);
        else if (!need && _hook != 0)
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0; _swallowUp = 0; _held = false;
        }
    }

    static void StartCapture()
    {
        if (_capturing) return;
        _capturing = true;
        UnregisterHotKey(_hwnd, 1);     // lets you re-enter the current combo
        SyncHook();
        SetTimer(_hwnd, 1, 10000, 0);
        Balloon("Press the new shortcut now (Esc cancels).");
    }

    static void EndCapture()
    {
        _capturing = false;
        KillTimer(_hwnd, 1);
        SyncHook();
    }

    [UnmanagedCallersOnly]
    static nint HookProc(int code, nuint wParam, nint lParam)
    {
        try
        {
            if (code >= 0)
            {
                var k = (KBDLLHOOKSTRUCT*)lParam;
                if ((k->flags & 0x10) == 0)   // ignore injected keys
                {
                    uint vk = k->vkCode;
                    bool down = wParam == 0x100 || wParam == 0x104;

                    if (!down && vk == _swallowUp) { _swallowUp = 0; _held = false; return 1; }

                    if (down && !IsModifier(vk))
                    {
                        uint m = CurrentMods();
                        if (_capturing)
                        {
                            if (vk == 0x1B && m == 0) { PostMessageW(_hwnd, WM_APP_CAPTURED, 0, 0); _swallowUp = vk; return 1; }
                            if (m != 0) { PostMessageW(_hwnd, WM_APP_CAPTURED, m, (nint)vk); _swallowUp = vk; return 1; }
                        }
                        else if (_useHook && vk == _vk && m == _mods)
                        {
                            if (!_held) { _held = true; PostMessageW(_hwnd, WM_APP_TOGGLE, (m & (MOD_ALT | MOD_WIN)) != 0 ? 1u : 0u, 0); }
                            _swallowUp = vk;
                            return 1;
                        }
                    }
                }
            }
        }
        catch { }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    static bool IsModifier(uint vk) => vk is (>= 0x10 and <= 0x12) or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    static uint CurrentMods()
    {
        uint m = 0;
        if (GetAsyncKeyState(0x12) < 0) m |= MOD_ALT;
        if (GetAsyncKeyState(0x11) < 0) m |= MOD_CTRL;
        if (GetAsyncKeyState(0x10) < 0) m |= MOD_SHIFT;
        if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) m |= MOD_WIN;
        return m;
    }

    /// <summary>Swallowing the key hides it from Windows, which would then open Start/menus on Win/Alt release.
    /// An unassigned keystroke in between prevents that.</summary>
    static void SendDummyKey()
    {
        INPUT* i = stackalloc INPUT[2];
        i[0] = new INPUT { type = 1, wVk = 0xE8 };
        i[1] = new INPUT { type = 1, wVk = 0xE8, dwFlags = 2 };
        SendInput(2, i, sizeof(INPUT));
    }

    // ------------------------------------------------------------------ tray + menu

    static void ShowMenu()
    {
        Refresh();
        nint m = CreatePopupMenu();
        string toggle = !_hdrAvail ? "No HDR display found" : _hdrOn ? "Turn HDR off" : "Turn HDR on";
        Add(m, _hdrAvail ? 0u : 1u, 1, toggle + "\t" + Format(_mods, _vk));
        AppendMenuW(m, 0x800, 0, null);
        Add(m, 0, 2, "Set shortcut…");
        Add(m, _mods == MOD_WIN && _vk == 'H' ? 8u : 0u, 3, "Use Win + H (replaces voice typing)");
        Add(m, _notify ? 8u : 0u, 4, "Show notifications");
        Add(m, AutostartOn() ? 8u : 0u, 5, "Start with Windows");
        AppendMenuW(m, 0x800, 0, null);
        Add(m, 0, 9, "Exit");

        POINT pt;
        GetCursorPos(&pt);
        SetForegroundWindow(_hwnd);   // required, or the menu won't close when you click away
        int cmd = TrackPopupMenuEx(m, 0x2 | 0x20 | 0x100, pt.x, pt.y, _hwnd, 0);   // right button | bottom align | return cmd
        PostMessageW(_hwnd, 0, 0, 0);
        DestroyMenu(m);

        switch (cmd)
        {
            case 1: Toggle(); break;
            case 2: StartCapture(); break;
            case 3: if (!(_mods == MOD_WIN && _vk == 'H')) SetHotkey(MOD_WIN, 'H'); break;
            case 4: _notify = !_notify; SaveSettings(); break;
            case 5: SetAutostart(!AutostartOn()); break;
            case 9: DestroyWindow(_hwnd); break;
        }
    }

    static void Add(nint menu, uint flags, nuint id, string text)
    {
        fixed (char* p = text) AppendMenuW(menu, flags, id, p);   // MF_STRING = 0
    }

    static void Tray(uint op)
    {
        var n = Nid();
        n.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        n.uCallbackMessage = WM_APP_TRAY;
        if (op != NIM_DELETE)
        {
            n.hIcon = CurrentIcon();
            Put(n.szTip, 128, !_hdrAvail ? "HDR Toggle: no HDR display found" : _hdrOn ? "HDR is on" : "HDR is off");
        }
        Shell_NotifyIconW(op, &n);
    }

    static void Balloon(string text)
    {
        var n = Nid();
        n.uFlags = NIF_INFO;
        n.dwInfoFlags = 0x10;   // no sound
        Put(n.szInfoTitle, 64, "HDR Toggle");
        Put(n.szInfo, 256, text);
        Shell_NotifyIconW(NIM_MODIFY, &n);
    }

    static NOTIFYICONDATAW Nid()
    {
        var n = new NOTIFYICONDATAW();
        n.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        n.hWnd = _hwnd;
        n.uID = 1;
        return n;
    }

    static void Put(char* dst, int cap, string s)
    {
        int len = Math.Min(s.Length, cap - 1);
        for (int i = 0; i < len; i++) dst[i] = s[i];
        dst[len] = '\0';
    }

    // ------------------------------------------------------------------ icons

    static nint CurrentIcon()
    {
        int i = (_light ? 2 : 0) + (_hdrOn ? 0 : 1);
        if (_icons[i] == 0) _icons[i] = LoadIco(IconNames[i], _iconSize);
        return _icons[i];
    }

    static void OnSettingChange()
    {
        bool light = IsLightTaskbar();
        int size = GetSystemMetrics(49);
        if (light == _light && size == _iconSize) return;
        _light = light; _iconSize = size;
        var old = _icons;
        _icons = new nint[4];
        Tray(NIM_MODIFY);
        foreach (var h in old) if (h != 0) DestroyIcon(h);
    }

    static bool IsLightTaskbar() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int v && v == 1;

    static nint LoadIco(string name, int want)
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream(name)!;
        var data = new byte[s.Length];
        s.ReadExactly(data);

        int count = data[4] | data[5] << 8, best = 0, bestScore = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int w = data[6 + 16 * i]; if (w == 0) w = 256;
            int score = w >= want ? w - want : 1000 + (want - w);
            if (score < bestScore) { bestScore = score; best = i; }
        }
        int e = 6 + 16 * best;
        uint size = BitConverter.ToUInt32(data, e + 8);
        int offset = BitConverter.ToInt32(data, e + 12);
        fixed (byte* p = &data[offset])
            return CreateIconFromResourceEx(p, size, 1, 0x30000, want, want, 0);
    }

    // ------------------------------------------------------------------ settings / autostart

    static void LoadSettings()
    {
        using var k = Registry.CurrentUser.OpenSubKey(AppKey);
        if (k?.GetValue("Key") is int key)
        {
            _vk = (uint)key;
            _mods = k.GetValue("Modifiers") is int m ? (uint)m : 0;
        }
        else _firstRun = true;
        _notify = k?.GetValue("Notify") is int n && n != 0;
    }

    static void SaveSettings()
    {
        using var k = Registry.CurrentUser.CreateSubKey(AppKey);
        k.SetValue("Key", (int)_vk, RegistryValueKind.DWord);
        k.SetValue("Modifiers", (int)_mods, RegistryValueKind.DWord);
        k.SetValue("Notify", _notify ? 1 : 0, RegistryValueKind.DWord);
    }

    static string Command => "\"" + Environment.ProcessPath + "\"";

    static bool AutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("HdrToggle") is string;
    }

    static void SetAutostart(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
        {
            run.SetValue("HdrToggle", Command);
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true);   // clear "disabled in Task Manager"
            approved?.DeleteValue("HdrToggle", false);
        }
        else run.DeleteValue("HdrToggle", false);
    }

    static void HealAutostart()   // exe moved? repoint the startup entry
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k?.GetValue("HdrToggle") is string s && s != Command) k.SetValue("HdrToggle", Command);
    }

    // ------------------------------------------------------------------ helpers

    static string Format(uint mods, uint vk)
    {
        if (vk == 0) return "None";
        string s = "";
        if ((mods & MOD_CTRL) != 0) s += "Ctrl+";
        if ((mods & MOD_ALT) != 0) s += "Alt+";
        if ((mods & MOD_SHIFT) != 0) s += "Shift+";
        if ((mods & MOD_WIN) != 0) s += "Win+";
        return s + KeyName(vk);
    }

    static string KeyName(uint vk)
    {
        if (vk is (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A)) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        bool ext = vk is (>= 0x21 and <= 0x28) or 0x2D or 0x2E;
        char* buf = stackalloc char[32];
        int n = GetKeyNameTextW((int)(MapVirtualKeyW(vk, 0) << 16) | (ext ? 1 << 24 : 0), buf, 32);
        return n > 0 ? new string(buf, 0, n) : "0x" + vk.ToString("X2");
    }

    static uint RegMsg(string s) { fixed (char* p = s) return RegisterWindowMessageW(p); }

    // ------------------------------------------------------------------ native

    struct POINT { public int x, y; }
    struct MSG { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public nuint extra; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(12)] public uint dwFlags;
    }

    struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeout;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [DllImport("user32.dll")] static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll")] static extern nint CreateWindowExW(uint ex, char* cls, char* title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern int DestroyWindow(nint h);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint h, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] static extern int GetMessageW(MSG* m, nint h, uint min, uint max);
    [DllImport("user32.dll")] static extern int TranslateMessage(MSG* m);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(MSG* m);
    [DllImport("user32.dll")] static extern int PostMessageW(nint h, uint msg, nuint w, nint l);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern uint RegisterWindowMessageW(char* s);
    [DllImport("user32.dll")] static extern int RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern int UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] static extern nint SetWindowsHookExW(int id, nint proc, nint mod, uint thread);
    [DllImport("user32.dll")] static extern int UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nuint w, nint l);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT* inputs, int size);
    [DllImport("user32.dll")] static extern nuint SetTimer(nint h, nuint id, uint ms, nint proc);
    [DllImport("user32.dll")] static extern int KillTimer(nint h, nuint id);
    [DllImport("user32.dll")] static extern nint CreatePopupMenu();
    [DllImport("user32.dll")] static extern int AppendMenuW(nint menu, uint flags, nuint id, char* text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint h, nint tpm);
    [DllImport("user32.dll")] static extern int DestroyMenu(nint menu);
    [DllImport("user32.dll")] static extern int GetCursorPos(POINT* p);
    [DllImport("user32.dll")] static extern int SetForegroundWindow(nint h);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern nint CreateIconFromResourceEx(byte* bits, uint size, int icon, uint ver, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int DestroyIcon(nint icon);
    [DllImport("user32.dll")] static extern uint MapVirtualKeyW(uint code, uint type);
    [DllImport("user32.dll")] static extern int GetKeyNameTextW(int lParam, char* buf, int size);
    [DllImport("kernel32.dll")] static extern nint GetModuleHandleW(char* name);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconW(uint msg, NOTIFYICONDATAW* data);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] static extern void FlushMenuThemes();
}

/// <summary>Reads and switches the "Use HDR" setting of every active HDR-capable display (CCD API).</summary>
internal static unsafe class Hdr
{
    struct Luid { public uint Lo; public int Hi; }
    struct Target { public Luid A; public uint Id; }
    struct Header { public int Type; public uint Size; public Luid Adapter; public uint Id; }
    struct Info1 { public Header H; public uint Value, Enc, Bits; }              // type 9  (legacy "advanced color")
    struct Info2 { public Header H; public uint Value, Enc, Bits, Mode; }        // type 15 (Windows 11 24H2+)
    struct SetValue { public Header H; public uint Value; }                      // type 10 (legacy) / 16 (24H2+)

    struct PathInfo
    {
        public Luid SAdapter; public uint SId, SMode, SStatus;
        public Luid TAdapter; public uint TId, TMode, Tech, Rotation, Scaling, RefNum, RefDen, Scan;
        public int TAvailable; public uint TStatus, Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    struct ModeInfo { public uint Type, Id; public Luid Adapter; }

    public static void GetState(out bool available, out bool on)
    {
        available = on = false;
        foreach (var t in Targets())
            if (Read(t, out bool sup, out bool en) && sup) { available = true; on |= en; }
    }

    /// <summary>Flips HDR. Returns 1 (now on), 0 (now off) or -1 (nothing changed).</summary>
    public static int Toggle()
    {
        var capable = new List<Target>();
        bool anyOn = false;
        foreach (var t in Targets())
            if (Read(t, out bool sup, out bool en) && sup) { capable.Add(t); anyOn |= en; }
        if (capable.Count == 0) return -1;

        bool want = !anyOn, ok = false;   // anything on -> all off, otherwise all on
        foreach (var t in capable) ok |= Write(t, want);
        return ok ? (want ? 1 : 0) : -1;
    }

    static bool Read(Target t, out bool supported, out bool enabled)
    {
        // 24H2+ separates "HDR" from "advanced color" (which stays on with Auto Color Management).
        var v2 = new Info2 { H = Mk(15, sizeof(Info2), t) };
        if (DisplayConfigGetDeviceInfo(&v2) == 0) { supported = (v2.Value & 0x10) != 0; enabled = (v2.Value & 0x20) != 0; return true; }

        var v1 = new Info1 { H = Mk(9, sizeof(Info1), t) };
        if (DisplayConfigGetDeviceInfo(&v1) == 0) { supported = (v1.Value & 1) != 0; enabled = (v1.Value & 2) != 0; return true; }

        supported = enabled = false;
        return false;
    }

    static bool Write(Target t, bool on)
    {
        var a = new SetValue { H = Mk(16, sizeof(SetValue), t), Value = on ? 1u : 0u };
        if (DisplayConfigSetDeviceInfo(&a) == 0) return true;
        var b = new SetValue { H = Mk(10, sizeof(SetValue), t), Value = on ? 1u : 0u };
        return DisplayConfigSetDeviceInfo(&b) == 0;
    }

    static Header Mk(int type, int size, Target t) => new() { Type = type, Size = (uint)size, Adapter = t.A, Id = t.Id };

    static List<Target> Targets()
    {
        var list = new List<Target>();
        uint np = 0, nm = 0;
        if (GetDisplayConfigBufferSizes(2, &np, &nm) != 0) return list;   // 2 = QDC_ONLY_ACTIVE_PATHS

        var paths = new PathInfo[np];
        var modes = new ModeInfo[nm];
        fixed (PathInfo* p = paths)
        fixed (ModeInfo* m = modes)
            if (QueryDisplayConfig(2, &np, p, &nm, m, 0) != 0) return list;

        for (int i = 0; i < np; i++)
        {
            var t = new Target { A = paths[i].TAdapter, Id = paths[i].TId };
            bool dup = false;
            foreach (var x in list) if (x.A.Lo == t.A.Lo && x.A.Hi == t.A.Hi && x.Id == t.Id) dup = true;
            if (!dup) list.Add(t);
        }
        return list;
    }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, uint* numPaths, uint* numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, uint* numPaths, PathInfo* paths, uint* numModes, ModeInfo* modes, nint topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(void* packet);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(void* packet);
}
