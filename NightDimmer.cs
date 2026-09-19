// Night Dimmer - dim, warm and blue-filter your screen for night viewing on Windows.
// (c) 2026 AI Education Labs - MIT License - https://github.com/AI-Education-Labs/night-dimmer
//
// Two layers: display gamma ramps (reach everything, incl. Alt+Tab/Start; Windows caps them at
// 50% brightness) plus a click-through topmost overlay per monitor for dimming beyond the cap.
// Lives in the system tray; single self-contained source file, builds with the csc.exe that
// ships with Windows.
//
// Build:   powershell -ExecutionPolicy Bypass -File build.ps1
// Hotkeys: Ctrl+Alt+D  panel     Ctrl+Alt+0  on/off     Ctrl+Alt+-/=  darker/brighter   Ctrl+Alt+B  screen off
//          Ctrl+Alt+9  warmth    Ctrl+Alt+8  blue cut   (alternates: PgDn/PgUp/End/Home)
// CLI:     NightDimmer.exe [--dim N] [--warmth N] [--blue N] [--blackout] [--on] [--off] [--toggle] [--panel] [--exit]
//          If an instance is already running the command is forwarded to it.
// Log:     %APPDATA%\NightDimmer.log

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Night Dimmer")]
[assembly: System.Reflection.AssemblyProduct("Night Dimmer")]
[assembly: System.Reflection.AssemblyDescription("Dim and warm your screen for night viewing")]
[assembly: System.Reflection.AssemblyCompany("AI Education Labs")]
[assembly: System.Reflection.AssemblyCopyright("Copyright © 2026 AI Education Labs. MIT License.")]
[assembly: System.Reflection.AssemblyVersion("1.2.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.2.1.0")]

namespace NightDimmer
{
    static class About
    {
        public const string Version = "1.2.1";
        public const string Company = "AI Education Labs";
        public const string Site = "https://aiedlabs.com";
        public const string Repo = "https://github.com/AI-Education-Labs/night-dimmer";
    }

    static class Native
    {
        public const int WS_EX_LAYERED     = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020; // clicks pass through
        public const int WS_EX_TOOLWINDOW  = 0x00000080; // hidden from Alt+Tab
        public const int WS_EX_NOACTIVATE  = 0x08000000; // never steals focus
        public const int WS_EX_TOPMOST     = 0x00000008;
        public const int CS_DROPSHADOW     = 0x00020000;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

        public const int WM_HOTKEY = 0x0312;
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;
        public const int WM_APP_CMD = 0x8000 + 1;
        public const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;
        public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002;
        public const string CTL_TITLE = "NightDimmerCtl";

        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_BORDER_COLOR = 34;
        public const int DWMWCP_ROUND = 2;

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ReleaseCapture();
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }

    static class Log
    {
        static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NightDimmer.log");
        public static void W(string msg)
        {
            try { System.IO.File.AppendAllText(Path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + "\r\n"); } catch { }
        }
    }

    // Commands accepted from the command line / a second instance.
    enum Cmd { Dim = 1, Warmth = 2, Toggle = 3, On = 4, Off = 5, Panel = 6, Exit = 7, Blue = 8, Blackout = 9 }

    // Low-level keyboard + mouse hooks, installed only while the screen is off. The first key
    // press wakes and is swallowed (so Space doesn't also unpause a video); mouse buttons wake and
    // are swallowed; mouse movement wakes after a short grace period (Windows itself wakes the
    // monitor on movement, so the overlay must lift at the same moment).
    // Hardware brightness for "screen off" without telling Windows the display is off (which on
    // managed / Modern Standby PCs triggers the lock screen). Laptop panels via WMI, external
    // monitors via DDC/CI. Previous levels are persisted so a crash mid-blackout can be undone.
    class Backlight
    {
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, out uint count);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetMonitorBrightness(IntPtr h, out uint min, out uint cur, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetMonitorBrightness(IntPtr h, uint value);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc fn, IntPtr data);
        delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR { public IntPtr hPhysicalMonitor; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string desc; }

        const string SavedKey = @"Software\NightDimmer\BacklightSaved";
        readonly object sync = new object();
        readonly List<KeyValuePair<string, uint>> saved = new List<KeyValuePair<string, uint>>(); // "wmi:<inst>" or "ddc:<n>" -> level
        volatile bool wantDark;

        // Lower everything as far as it goes, on a worker thread (DDC/CI is slow and must not block
        // the UI thread, which services the wake hooks). A Restore() call during the dim cancels it.
        public void DimAsync()
        {
            wantDark = true;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                lock (sync)
                {
                    if (!wantDark || saved.Count > 0) return;
                    try { DimCore(); } catch (Exception ex) { Log.W("backlight dim: " + ex.Message); }
                    if (!wantDark) RestoreCore();
                }
            });
        }

        public void Restore()
        {
            wantDark = false;
            lock (sync) RestoreCore();
        }

        void DimCore()
        {
            // laptop / tablet panel
            try
            {
                foreach (KeyValuePair<string, uint> kv in ReadWmi())
                {
                    saved.Add(new KeyValuePair<string, uint>("wmi:" + kv.Key, kv.Value));
                    Persist();
                    if (!wantDark) return;
                    SetWmi(kv.Key, 0);
                }
            }
            catch (Exception ex) { Log.W("wmi brightness: " + ex.Message); }
            // external monitors (DDC/CI)
            int n = 0;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data)
            {
                if (!wantDark) return false;
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0) return true;
                PHYSICAL_MONITOR[] pm = new PHYSICAL_MONITOR[count];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, pm)) return true;
                for (int i = 0; i < count; i++)
                {
                    uint min, cur, max;
                    if (GetMonitorBrightness(pm[i].hPhysicalMonitor, out min, out cur, out max))
                    {
                        saved.Add(new KeyValuePair<string, uint>("ddc:" + n, cur));
                        Persist();
                        if (!SetMonitorBrightness(pm[i].hPhysicalMonitor, min)) Log.W("ddc dim failed on monitor " + n + " err=" + Marshal.GetLastWin32Error());
                    }
                    n++;
                }
                DestroyPhysicalMonitors(count, pm);
                return true;
            }, IntPtr.Zero);
            Log.W("backlight dimmed on " + saved.Count + " device(s)");
        }

        void RestoreCore()
        {
            if (saved.Count == 0) return;
            bool ok = true;
            foreach (KeyValuePair<string, uint> kv in saved)
                if (kv.Key.StartsWith("wmi:"))
                {
                    try
                    {
                        uint now = 0;
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            SetWmi(kv.Key.Substring(4), kv.Value);
                            System.Threading.Thread.Sleep(300); // WMI reports the old level for a moment
                            now = 0;
                            foreach (KeyValuePair<string, uint> r in ReadWmi()) if (r.Key == kv.Key.Substring(4)) now = r.Value;
                            if (now == kv.Value) break;
                        }
                        if (now != kv.Value) { ok = false; Log.W("wmi restore: asked " + kv.Value + ", panel reports " + now); }
                    }
                    catch (Exception ex) { ok = false; Log.W("backlight restore wmi failed: " + ex.Message); }
                }
            Dictionary<int, uint> ddc = new Dictionary<int, uint>(); // monitor index -> level
            foreach (KeyValuePair<string, uint> kv in saved) if (kv.Key.StartsWith("ddc:")) ddc[int.Parse(kv.Key.Substring(4))] = kv.Value;
            if (ddc.Count > 0)
            {
                int n = 0;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data)
                {
                    uint count;
                    if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out count) || count == 0) return true;
                    PHYSICAL_MONITOR[] pm = new PHYSICAL_MONITOR[count];
                    if (!GetPhysicalMonitorsFromHMONITOR(hMon, count, pm)) return true;
                    for (int i = 0; i < count; i++, n++)
                    {
                        uint level;
                        if (!ddc.TryGetValue(n, out level)) continue; // this monitor never answered DDC (e.g. laptop panel)
                        if (SetMonitorBrightness(pm[i].hPhysicalMonitor, level)) continue;
                        int err = Marshal.GetLastWin32Error();
                        System.Threading.Thread.Sleep(150); // DDC/CI is flaky right after another command
                        if (!SetMonitorBrightness(pm[i].hPhysicalMonitor, level)) { ok = false; Log.W("ddc restore failed on monitor " + n + " err=" + err); }
                    }
                    DestroyPhysicalMonitors(count, pm);
                    return true;
                }, IntPtr.Zero);
            }
            Log.W("backlight restored (" + saved.Count + " device(s)" + (ok ? "" : ", with errors") + ")");
            if (ok)
            {
                saved.Clear();
                try { Registry.CurrentUser.DeleteSubKey(SavedKey, false); } catch { }
            }
            // on failure the saved levels stay on disk so the next start can retry
        }

        // Called at startup: if a previous run died while dimmed, put the levels back.
        public void RecoverIfNeeded()
        {
            lock (sync)
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SavedKey))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                        saved.Add(new KeyValuePair<string, uint>(name, Convert.ToUInt32(k.GetValue(name))));
                }
                if (saved.Count > 0) { Log.W("recovering backlight from previous run"); RestoreCore(); }
            }
        }

        void Persist()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SavedKey))
                    foreach (KeyValuePair<string, uint> kv in saved) k.SetValue(kv.Key, (int)kv.Value, RegistryValueKind.DWord);
            }
            catch { }
        }

        // WMI's COM objects are apartment-bound and our wake path runs inside a low-level hook
        // callback (RPC_E_WRONG_THREAD otherwise), so every WMI call gets its own MTA thread.
        static void OnMta(Action work)
        {
            Exception err = null;
            System.Threading.Thread t = new System.Threading.Thread(delegate() { try { work(); } catch (Exception ex) { err = ex; } });
            t.SetApartmentState(System.Threading.ApartmentState.MTA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(4000)) throw new TimeoutException("WMI call timed out");
            if (err != null) throw err;
        }

        static List<KeyValuePair<string, uint>> ReadWmi()
        {
            List<KeyValuePair<string, uint>> list = new List<KeyValuePair<string, uint>>();
            OnMta(delegate
            {
                using (var cls = new System.Management.ManagementClass(@"root\WMI", "WmiMonitorBrightness", null))
                foreach (System.Management.ManagementObject o in cls.GetInstances())
                    list.Add(new KeyValuePair<string, uint>((string)o["InstanceName"], Convert.ToUInt32(o["CurrentBrightness"])));
            });
            return list;
        }

        static void SetWmi(string instance, uint level)
        {
            OnMta(delegate
            {
                using (var cls = new System.Management.ManagementClass(@"root\WMI", "WmiMonitorBrightnessMethods", null))
                foreach (System.Management.ManagementObject o in cls.GetInstances())
                    if ((string)o["InstanceName"] == instance)
                        o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)level });
            });
        }
    }

    class InputWatch : IDisposable
    {
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr hMod, uint tid);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int nCode, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_XBUTTONDOWN = 0x20B, WM_MOUSEWHEEL = 0x20A;

        readonly HookProc kbProc, msProc; // kept alive: delegates must not be collected while hooked
        IntPtr kb, ms;
        bool armed;
        int swallowUpVk = -1;
        DateTime moveArmAt;
        int lastX = int.MinValue, lastY;
        public event Action<string> Woken;

        public InputWatch() { kbProc = KbCallback; msProc = MsCallback; }

        public bool Start(int mouseGraceMs)
        {
            armed = true;
            swallowUpVk = -1;
            lastX = int.MinValue;
            moveArmAt = DateTime.UtcNow.AddMilliseconds(mouseGraceMs);
            IntPtr mod = GetModuleHandle(null);
            if (kb == IntPtr.Zero) kb = SetWindowsHookEx(WH_KEYBOARD_LL, kbProc, mod, 0);
            if (ms == IntPtr.Zero) ms = SetWindowsHookEx(WH_MOUSE_LL, msProc, mod, 0);
            if (kb == IntPtr.Zero) Log.W("keyboard hook failed: " + Marshal.GetLastWin32Error());
            if (ms == IntPtr.Zero) Log.W("mouse hook failed: " + Marshal.GetLastWin32Error());
            return kb != IntPtr.Zero;
        }

        public void Stop()
        {
            armed = false;
            if (ms != IntPtr.Zero) { UnhookWindowsHookEx(ms); ms = IntPtr.Zero; }
            if (swallowUpVk < 0 && kb != IntPtr.Zero) { UnhookWindowsHookEx(kb); kb = IntPtr.Zero; }
            // else: keyboard hook lingers just long enough to eat the matching key-up
        }

        void Fire(string why)
        {
            armed = false;
            if (Woken != null) Woken(why);
        }

        IntPtr KbCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
                if (armed && (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN))
                {
                    swallowUpVk = vk;
                    Fire("key vk=" + vk);
                    return new IntPtr(1);
                }
                if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && vk == swallowUpVk)
                {
                    swallowUpVk = -1;
                    if (!armed && kb != IntPtr.Zero) { UnhookWindowsHookEx(kb); kb = IntPtr.Zero; }
                    return new IntPtr(1);
                }
            }
            return CallNextHookEx(kb, nCode, wParam, lParam);
        }

        IntPtr MsCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && armed)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN || msg == WM_MBUTTONDOWN || msg == WM_XBUTTONDOWN || msg == WM_MOUSEWHEEL)
                {
                    Fire("mouse button 0x" + msg.ToString("X"));
                    return new IntPtr(1);
                }
                if (msg == WM_MOUSEMOVE)
                {
                    int x = Marshal.ReadInt32(lParam), y = Marshal.ReadInt32(lParam, 4); // MSLLHOOKSTRUCT.pt
                    if (lastX == int.MinValue) { lastX = x; lastY = y; }
                    else if (DateTime.UtcNow >= moveArmAt && (Math.Abs(x - lastX) > 12 || Math.Abs(y - lastY) > 12)) Fire("mouse move " + (x - lastX) + "," + (y - lastY));
                }
            }
            return CallNextHookEx(ms, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            armed = false; swallowUpVk = -1;
            if (ms != IntPtr.Zero) { UnhookWindowsHookEx(ms); ms = IntPtr.Zero; }
            if (kb != IntPtr.Zero) { UnhookWindowsHookEx(kb); kb = IntPtr.Zero; }
        }
    }


    // =====================================================================
    //  Gamma ramps: dim/warm at the display level so shell UI that sits above
    //  every window (Alt+Tab, Start, notifications) is filtered too.
    //  Windows caps ramps at ~50% brightness unless GdiIcmGammaRange=256 is
    //  set in HKLM; we learn the floor by trying and fall back to the overlay.
    // =====================================================================
    class Gamma
    {
        [DllImport("gdi32.dll")] static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
        [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr mode);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);

        class Dev { public string Name; public IntPtr Dc; public ushort[] Baseline, Last; }
        readonly List<Dev> devs = new List<Dev>();
        bool applied;

        // 0 = unrestricted (or not yet known), 0.5 = Windows default cap, 1 = gamma unsupported here
        public double Floor = 0;
        public bool Supported { get { return Floor < 1 && devs.Count > 0; } }

        public Gamma() { Refresh(); }

        public void Refresh()
        {
            if (applied) Restore();
            foreach (Dev d in devs) if (d.Dc != IntPtr.Zero) DeleteDC(d.Dc);
            devs.Clear();
            foreach (Screen sc in Screen.AllScreens)
            {
                IntPtr dc = CreateDC(null, sc.DeviceName, null, IntPtr.Zero);
                if (dc == IntPtr.Zero) continue;
                ushort[] r = new ushort[768];
                if (!GetDeviceGammaRamp(dc, r)) { DeleteDC(dc); continue; }
                Dev d = new Dev(); d.Name = sc.DeviceName; d.Dc = dc; d.Baseline = r;
                devs.Add(d);
            }
            if (devs.Count == 0) Floor = 1;
            Log.W("gamma devices=" + devs.Count);
        }

        // Applies channel multipliers (0..1). Returns false if the driver rejected them.
        bool Set(double r, double g, double b)
        {
            bool all = true;
            foreach (Dev d in devs)
            {
                ushort[] ramp = new ushort[768];
                for (int i = 0; i < 256; i++)
                {
                    ramp[i]       = (ushort)Math.Min(65535, d.Baseline[i] * r);
                    ramp[256 + i] = (ushort)Math.Min(65535, d.Baseline[256 + i] * g);
                    ramp[512 + i] = (ushort)Math.Min(65535, d.Baseline[512 + i] * b);
                }
                if (SetDeviceGammaRamp(d.Dc, ramp)) d.Last = ramp; else all = false;
            }
            return all;
        }

        // Colour (warmth = amber tone, blue = blue-channel cut) goes through gamma first, then as much
        // dimming as the cap allows. Returns the brightness achieved so the overlay can make up the rest.
        public double Apply(double brightness, double warmth, double blue)
        {
            if (!Supported) return 1;
            double gMul = 1 - 0.25 * warmth;
            double bMul = (1 - 0.5 * warmth) * (1 - 0.7 * blue);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                double g = gMul, b = bMul, bg = brightness;
                if (Floor > 0)
                {
                    double fl = Floor + 0.003; // stay clear of the cap after rounding
                    g = Math.Max(g, fl);
                    b = Math.Max(b, fl);
                    bg = Math.Min(1, Math.Max(brightness, fl / Math.Min(g, b)));
                }
                if (Set(bg, bg * g, bg * b)) { applied = true; return bg; }
                Floor = Floor <= 0 ? 0.5 : 1;
                Log.W("gamma ramp rejected -> floor now " + Floor);
            }
            Restore();
            return 1;
        }

        public bool Capped { get { return Supported && Floor >= 0.5; } }

        public void Reapply() { if (applied) foreach (Dev d in devs) if (d.Last != null) SetDeviceGammaRamp(d.Dc, d.Last); }

        public void Restore()
        {
            foreach (Dev d in devs) SetDeviceGammaRamp(d.Dc, d.Baseline);
            applied = false;
        }
    }

    // =====================================================================
    //  Theme + custom controls
    // =====================================================================

    static class Theme
    {
        public static readonly Color Bg      = Color.FromArgb(22, 19, 16);
        public static readonly Color Surface = Color.FromArgb(36, 31, 26);
        public static readonly Color Hover   = Color.FromArgb(48, 41, 34);
        public static readonly Color Border  = Color.FromArgb(62, 53, 44);
        public static readonly Color Track   = Color.FromArgb(66, 57, 47);
        public static readonly Color Accent  = Color.FromArgb(255, 176, 72);
        public static readonly Color Accent2 = Color.FromArgb(255, 118, 40);
        public static readonly Color Text    = Color.FromArgb(242, 234, 222);
        public static readonly Color Muted   = Color.FromArgb(152, 140, 126);

        public static Font Font(float pt, FontStyle st)
        {
            string[] names = { "Segoe UI Variable Text", "Segoe UI", "Tahoma" };
            foreach (string n in names)
            {
                try { using (FontFamily ff = new FontFamily(n)) if (ff.IsStyleAvailable(st)) return new Font(n, pt, st); }
                catch { }
            }
            return new Font(SystemFonts.DefaultFont.FontFamily, pt, st);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            if (d <= 0 || r.Width <= 0 || r.Height <= 0) { p.AddRectangle(r); return p; }
            d = Math.Min(d, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Lerp(Color a, Color b, float t)
        {
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static void DrawMoon(Graphics g, RectangleF r, Color color, Color bg, bool glow)
        {
            if (glow)
            {
                RectangleF gr = RectangleF.Inflate(r, r.Width * 0.35f, r.Height * 0.35f);
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(gr);
                    using (PathGradientBrush pb = new PathGradientBrush(gp))
                    {
                        pb.CenterColor = Color.FromArgb(70, color);
                        pb.SurroundColors = new Color[] { Color.FromArgb(0, color) };
                        g.FillEllipse(pb, gr);
                    }
                }
            }
            using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, r);
            RectangleF bite = new RectangleF(r.X + r.Width * 0.32f, r.Y - r.Height * 0.18f, r.Width, r.Height);
            using (SolidBrush b = new SolidBrush(bg)) g.FillEllipse(b, bite);
        }
    }

    class Slider : Control
    {
        int min, max = 100, val;
        bool dragging, hover;
        public event EventHandler ValueChanged;
        public int Step = 5;

        public Slider()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            BackColor = Theme.Bg;
        }

        public int Minimum { get { return min; } set { min = value; Invalidate(); } }
        public int Maximum { get { return max; } set { max = value; Invalidate(); } }
        public int Value
        {
            get { return val; }
            set
            {
                int v = value < min ? min : (value > max ? max : value);
                if (v == val) return;
                val = v;
                Invalidate();
                if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            }
        }
        public void SetSilent(int v) { val = v < min ? min : (v > max ? max : v); Invalidate(); }

        float K { get { return DeviceDpi / 96f; } }
        int Pad { get { return (int)(11 * K); } }

        void SetFromX(int x)
        {
            float frac = (x - Pad) / (float)Math.Max(1, Width - 2 * Pad);
            frac = frac < 0 ? 0 : (frac > 1 ? 1 : frac);
            Value = min + (int)Math.Round(frac * (max - min));
        }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); dragging = true; SetFromX(e.X); base.OnMouseDown(e); }
        protected override void OnMouseMove(MouseEventArgs e) { if (dragging) SetFromX(e.X); base.OnMouseMove(e); }
        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta > 0 ? Step : -Step; base.OnMouseWheel(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k)
        {
            return k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down || base.IsInputKey(k);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down: Value -= Step; e.Handled = true; break;
                case Keys.Right: case Keys.Up: Value += Step; e.Handled = true; break;
                case Keys.PageDown: Value -= Step * 2; e.Handled = true; break;
                case Keys.PageUp: Value += Step * 2; e.Handled = true; break;
                case Keys.Home: Value = min; e.Handled = true; break;
                case Keys.End: Value = max; e.Handled = true; break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            float k = K;
            int th = (int)(6 * k), r = (int)(9 * k), pad = Pad;
            float cy = Height / 2f;
            RectangleF track = new RectangleF(pad, cy - th / 2f, Width - 2 * pad, th);
            using (GraphicsPath p = Theme.Round(track, th / 2f))
            using (SolidBrush b = new SolidBrush(Theme.Track)) g.FillPath(b, p);

            float frac = max > min ? (val - min) / (float)(max - min) : 0;
            float fx = pad + frac * (Width - 2 * pad);
            if (fx - pad > 1)
            {
                RectangleF fill = new RectangleF(pad, cy - th / 2f, fx - pad, th);
                using (GraphicsPath p = Theme.Round(fill, th / 2f))
                using (LinearGradientBrush lb = new LinearGradientBrush(new RectangleF(pad - 1, 0, Math.Max(2, Width - 2 * pad + 2), 1), Theme.Accent2, Theme.Accent, 0f))
                    g.FillPath(lb, p);
            }

            bool lit = hover || dragging || Focused;
            if (lit)
            {
                float gr = r + 6 * k;
                using (SolidBrush b = new SolidBrush(Color.FromArgb(dragging ? 70 : 40, Theme.Accent)))
                    g.FillEllipse(b, fx - gr, cy - gr, gr * 2, gr * 2);
            }
            using (SolidBrush b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, fx - r, cy - r, r * 2, r * 2);
            float ir = r - 3 * k;
            using (SolidBrush b = new SolidBrush(lit ? Theme.Accent : Theme.Bg)) g.FillEllipse(b, fx - ir, cy - ir, ir * 2, ir * 2);
        }
    }

    class Toggle : Control
    {
        bool chk, hover;
        float anim;
        readonly Timer t = new Timer();
        public event EventHandler CheckedChanged;

        public Toggle()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            t.Interval = 12;
            t.Tick += delegate
            {
                float target = chk ? 1 : 0;
                anim += (target - anim) * 0.35f;
                if (Math.Abs(target - anim) < 0.02f) { anim = target; t.Stop(); }
                Invalidate();
            };
        }

        public bool Checked
        {
            get { return chk; }
            set
            {
                if (chk == value) return;
                chk = value;
                t.Start();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }
        public void SetSilent(bool v) { if (chk == v) return; chk = v; anim = v ? 1 : 0; Invalidate(); }

        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            float h = Height - 1, w = Width - 1;
            RectangleF pill = new RectangleF(0.5f, 0.5f, w, h);
            Color fill = Theme.Lerp(Theme.Track, Theme.Accent, anim);
            if (hover && !chk) fill = Theme.Lerp(fill, Theme.Border, 0.5f);
            if (anim > 0.05f)
            {
                using (GraphicsPath gp = Theme.Round(RectangleF.Inflate(pill, 3, 3), h / 2 + 3))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(45 * anim), Theme.Accent))) g.FillPath(b, gp);
            }
            using (GraphicsPath gp = Theme.Round(pill, h / 2))
            using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, gp);
            float d = h - 6;
            float x = 3.5f + anim * (w - h);
            Color knob = Theme.Lerp(Theme.Muted, Theme.Bg, anim);
            using (SolidBrush b = new SolidBrush(knob)) g.FillEllipse(b, x, 3.5f, d, d);
        }
    }

    class Pill : Control
    {
        bool hover, down, active;
        public bool Borderless;
        public bool Danger; // hover turns red: destructive action
        public bool Active { get { return active; } set { active = value; Invalidate(); } }

        public Pill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            Font = Theme.Font(9.5f, FontStyle.Regular);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fill = down ? Theme.Hover : (hover ? Theme.Surface : (active ? Theme.Surface : BackColor));
            Color border = active ? Theme.Accent : (Borderless ? Color.Transparent : Theme.Border);
            Color text = active ? Theme.Accent : (hover ? Theme.Text : Theme.Muted);
            if (Danger && hover) { fill = Color.FromArgb(70, 200, 60, 50); text = Color.FromArgb(255, 120, 110); }
            using (GraphicsPath gp = Theme.Round(r, Height / 2f))
            {
                if (fill != BackColor || active) using (SolidBrush b = new SolidBrush(active && !hover ? Color.FromArgb(28, Theme.Accent) : fill)) g.FillPath(b, gp);
                if (border.A > 0) using (Pen p = new Pen(border, 1f)) g.DrawPath(p, gp);
            }
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // =====================================================================
    //  Overlay, hotkeys, settings
    // =====================================================================

    // One of these per monitor.
    class OverlayForm : Form
    {
        public OverlayForm(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            Opacity = 0.5;
        }

        bool blackout;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                if (!blackout) cp.ExStyle |= Native.WS_EX_TRANSPARENT; // click-through, except when blacked out
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void ReassertTopmost()
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        // Blackout: opaque black, and the window takes the mouse so the cursor can be hidden and a
        // click can end it. (Keyboard is handled by the low-level hook since we never take focus.)
        static Cursor blank;
        public event Action Clicked;

        public void SetBlackout(bool on)
        {
            blackout = on;
            UpdateStyles(); // re-applies CreateParams (WinForms does this itself on Opacity changes)
            if (on)
            {
                if (blank == null)
                    using (Bitmap b = new Bitmap(32, 32)) blank = new Cursor(b.GetHicon());
                Cursor = blank;
                BackColor = Color.Black;
                Opacity = 1;
                if (!Visible) Show();
            }
            else Cursor = Cursors.Default;
            ReassertTopmost();
        }

        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Clicked != null) Clicked(); }
    }

    // Invisible, named window: receives WM_HOTKEY and commands from other instances.
    class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int> Pressed;
        public event Action<Cmd, int> Command;
        readonly List<int> ids = new List<int>();

        public HotkeyWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = Native.CTL_TITLE;
            CreateHandle(cp);
        }

        public bool Register(int id, uint mods, Keys key)
        {
            ids.Add(id);
            return Native.RegisterHotKey(Handle, id, mods, (uint)key);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && Pressed != null) Pressed(m.WParam.ToInt32());
            else if (m.Msg == Native.WM_APP_CMD && Command != null) Command((Cmd)m.WParam.ToInt32(), m.LParam.ToInt32());
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            foreach (int id in ids) Native.UnregisterHotKey(Handle, id);
            DestroyHandle();
        }
    }

    class Settings
    {
        const string KeyPath = @"Software\NightDimmer";
        public int Dim = 50;       // 0..90 %  (never 100 so you can't lock yourself out)
        public int Warmth = 40;    // 0..100 %  amber tone
        public int Blue = 30;      // 0..100 %  blue-light cut
        public bool Enabled = true;
        public bool PowerOff = true;   // Screen off also sends DPMS power-off (auto-disabled if that locks the PC)
        public bool FirstRun = true;

        public static Settings Load()
        {
            Settings s = new Settings();
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
            {
                if (k == null) return s;
                s.Dim      = Clamp(Convert.ToInt32(k.GetValue("Dim", s.Dim)), 0, TrayApp.MAX_DIM);
                s.Warmth   = Clamp(Convert.ToInt32(k.GetValue("Warmth", s.Warmth)), 0, 100);
                s.Blue     = Clamp(Convert.ToInt32(k.GetValue("Blue", s.Blue)), 0, 100);
                s.PowerOff = Convert.ToInt32(k.GetValue("PowerOff", 1)) != 0;
                s.Enabled  = Convert.ToInt32(k.GetValue("Enabled", 1)) != 0;
                s.FirstRun = Convert.ToInt32(k.GetValue("FirstRun", 1)) != 0;
            }
            return s;
        }

        public void Save()
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                k.SetValue("Dim", Dim);
                k.SetValue("Warmth", Warmth);
                k.SetValue("Blue", Blue);
                k.SetValue("PowerOff", PowerOff ? 1 : 0);
                k.SetValue("Enabled", Enabled ? 1 : 0);
                k.SetValue("FirstRun", FirstRun ? 1 : 0);
            }
        }

        public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }

    // =====================================================================
    //  Control panel  (Ctrl+Alt+D)
    // =====================================================================

    class PanelForm : Form
    {
        static readonly string[] PresetNames = { "Subtle", "Movie", "Cave" };
        static readonly int[] PresetDim  = { 30, 60, 85 };
        static readonly int[] PresetWarm = { 20, 45, 65 };
        static readonly int[] PresetBlue = { 20, 40, 60 };

        readonly Settings s;
        readonly Action changed, exit, unlock;
        readonly Func<bool> getStartup, isCapped;
        readonly Action<bool> setStartup;
        readonly float k;
        readonly int W = 380, P = 24;

        readonly Slider dim, warm, blue;
        readonly Toggle master, startup, power;
        readonly Pill[] presets = new Pill[3];
        readonly Pill unlockLink;
        readonly Font fTitle, fSub, fLabel, fSection, fValue, fHint;
        readonly Timer fade = new Timer();
        bool syncing;

        // y positions (unscaled)
        const int Y_HEADER = 22, Y_SUB = 54, Y_MASTER = 92, Y_DIM = 142, Y_WARM = 208, Y_BLUE = 274, Y_PRESET = 340,
                  Y_BLACKOUT = 386, Y_UNLOCK = 434, Y_DIV = 464, Y_START = 480, Y_POWER = 514, Y_FOOT = 562, Y_CREDIT = 598, H = 628;

        public PanelForm(Settings settings, int maxDim, Action onChanged, Action onExit, Func<bool> isStartup, Action<bool> setStartupFn,
                         Func<bool> gammaCapped, Action unlockGamma, Action blackoutFn)
        {
            s = settings; changed = onChanged; exit = onExit; getStartup = isStartup; setStartup = setStartupFn;
            isCapped = gammaCapped; unlock = unlockGamma;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Text = "Night Dimmer";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            KeyPreview = true;
            Opacity = 0;

            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            ClientSize = new Size(S(W), S(H));

            fTitle = Theme.Font(15f, FontStyle.Bold);
            fSub = Theme.Font(9f, FontStyle.Regular);
            fLabel = Theme.Font(11f, FontStyle.Regular);
            fSection = Theme.Font(8f, FontStyle.Bold);
            fValue = Theme.Font(11f, FontStyle.Bold);
            fHint = Theme.Font(8f, FontStyle.Regular);

            // X quits the whole app (the footer "Hide" just dismisses the panel)
            Pill close = new Pill(); close.Text = "✕"; close.Borderless = true; close.Danger = true;
            close.Font = Theme.Font(10f, FontStyle.Regular);
            close.SetBounds(S(W - P - 24), S(Y_HEADER - 6), S(30), S(30));
            close.Click += delegate { exit(); };
            Controls.Add(close);
            ToolTip tip = new ToolTip(); tip.SetToolTip(close, "Quit Night Dimmer (turns the filter off)");

            // master toggle
            master = new Toggle(); master.SetBounds(S(W - P - 46), S(Y_MASTER + 1), S(46), S(26));
            master.CheckedChanged += delegate { if (syncing) return; s.Enabled = master.Checked; if (s.Enabled && s.Dim == 0) s.Dim = 30; changed(); };
            Controls.Add(master);

            // sliders
            dim = new Slider(); dim.Minimum = 0; dim.Maximum = maxDim; dim.SetBounds(S(P - 11), S(Y_DIM + 22), S(W - 2 * P + 22), S(32));
            dim.ValueChanged += delegate { if (syncing) return; s.Dim = dim.Value; if (s.Dim > 0) s.Enabled = true; changed(); };
            Controls.Add(dim);

            warm = new Slider(); warm.Minimum = 0; warm.Maximum = 100; warm.SetBounds(S(P - 11), S(Y_WARM + 22), S(W - 2 * P + 22), S(32));
            warm.ValueChanged += delegate { if (syncing) return; s.Warmth = warm.Value; changed(); };
            Controls.Add(warm);

            blue = new Slider(); blue.Minimum = 0; blue.Maximum = 100; blue.SetBounds(S(P - 11), S(Y_BLUE + 22), S(W - 2 * P + 22), S(32));
            blue.ValueChanged += delegate { if (syncing) return; s.Blue = blue.Value; changed(); };
            Controls.Add(blue);

            // presets
            int gap = 10, pw = (W - 2 * P - 2 * gap) / 3;
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                Pill p = new Pill(); p.Text = PresetNames[i];
                p.SetBounds(S(P + i * (pw + gap)), S(Y_PRESET), S(pw), S(32));
                p.Click += delegate { s.Dim = PresetDim[idx]; s.Warmth = PresetWarm[idx]; s.Blue = PresetBlue[idx]; s.Enabled = true; changed(); };
                presets[i] = p;
                Controls.Add(p);
            }

            // full blackout - any key or click wakes the screen
            Pill black = new Pill(); black.Text = "⏻   Turn screen off   ·   any key or mouse wakes it";
            black.Font = Theme.Font(9.5f, FontStyle.Regular);
            black.SetBounds(S(P), S(Y_BLACKOUT), S(W - 2 * P), S(36));
            black.Click += delegate { blackoutFn(); };
            Controls.Add(black);
            tip.SetToolTip(black, "Powers off every monitor (Ctrl+Alt+B). Any key, click or mouse movement wakes them.");

            // shown only while Windows caps gamma at 50%
            unlockLink = new Pill(); unlockLink.Text = "Unlock full range (admin)"; unlockLink.Borderless = true;
            unlockLink.Font = Theme.Font(8f, FontStyle.Underline);
            unlockLink.SetBounds(S(W - P - 160), S(Y_UNLOCK), S(160), S(20));
            unlockLink.Click += delegate { unlock(); };
            Controls.Add(unlockLink);

            // startup toggle
            power = new Toggle(); power.SetBounds(S(W - P - 40), S(Y_POWER), S(40), S(22));
            power.CheckedChanged += delegate { if (syncing) return; s.PowerOff = power.Checked; changed(); };
            Controls.Add(power);
            tip.SetToolTip(power, "Off: Screen off only dims the backlight and blacks out - use this if your PC shows the sign-in screen on wake.");

            startup = new Toggle(); startup.SetBounds(S(W - P - 40), S(Y_START), S(40), S(22));
            startup.CheckedChanged += delegate { if (syncing) return; setStartup(startup.Checked); };
            Controls.Add(startup);

            // hide (filter keeps running)
            Pill hide = new Pill(); hide.Text = "Hide"; hide.Font = Theme.Font(9f, FontStyle.Regular);
            hide.SetBounds(S(W - P - 64), S(Y_FOOT - 2), S(64), S(26));
            hide.Click += delegate { Close(); };
            Controls.Add(hide);
            tip.SetToolTip(hide, "Close this window; the filter stays on (Esc)");

            // credit / link to the makers
            Pill credit = new Pill(); credit.Borderless = true; credit.Font = Theme.Font(8f, FontStyle.Regular);
            credit.Text = "Made by " + About.Company + "  ·  aiedlabs.com";
            Size csz = TextRenderer.MeasureText(credit.Text, credit.Font);
            credit.SetBounds(S(P) - S(8), S(Y_CREDIT), csz.Width + S(16), S(22));
            credit.Click += delegate { try { System.Diagnostics.Process.Start(About.Site); } catch { } };
            Controls.Add(credit);
            tip.SetToolTip(credit, "Free & open source (MIT) — " + About.Repo);

            fade.Interval = 12;
            fade.Tick += delegate { Opacity = Math.Min(1, Opacity + 0.12); if (Opacity >= 1) fade.Stop(); };

            PlaceNearTray();
        }

        int S(int v) { return (int)Math.Round(v * k); }

        void PlaceNearTray()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Rectangle b = Screen.PrimaryScreen.Bounds;
            int margin = S(16);
            int bottomMargin = wa.Height == b.Height ? S(64) : margin; // auto-hidden taskbar: leave room
            Location = new Point(wa.Right - Width - margin, wa.Bottom - Height - bottomMargin);
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= Native.CS_DROPSHADOW; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int pref = Native.DWMWCP_ROUND;
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
                int col = Theme.Border.R | (Theme.Border.G << 8) | (Theme.Border.B << 16);
                Native.DwmSetWindowAttribute(Handle, Native.DWMWA_BORDER_COLOR, ref col, 4);
            }
            catch { }
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); fade.Start(); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        // drag anywhere on the chrome
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                Native.ReleaseCapture();
                Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, new IntPtr(Native.HTCAPTION), IntPtr.Zero);
            }
        }

        public void Sync()
        {
            syncing = true;
            dim.SetSilent(s.Dim);
            warm.SetSilent(s.Warmth);
            blue.SetSilent(s.Blue);
            master.SetSilent(s.Enabled);
            startup.SetSilent(getStartup());
            power.SetSilent(s.PowerOff);
            unlockLink.Visible = isCapped();
            for (int i = 0; i < 3; i++)
                presets[i].Active = s.Enabled && s.Dim == PresetDim[i] && s.Warmth == PresetWarm[i] && s.Blue == PresetBlue[i];
            syncing = false;
            Invalidate();
        }

        void Txt(Graphics g, string t, Font f, int x, int y, Color c)
        {
            TextRenderer.DrawText(g, t, f, new Point(S(x), S(y)), c, TextFormatFlags.NoPadding);
        }
        void TxtRight(Graphics g, string t, Font f, int right, int y, Color c)
        {
            Size sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, t, f, new Point(S(right) - sz.Width, S(y)), c, TextFormatFlags.NoPadding);
        }
        void Section(Graphics g, string name, string value, int y)
        {
            Txt(g, name.ToUpperInvariant(), fSection, P, y + 4, Theme.Muted);
            TxtRight(g, value, fValue, W - P, y - 1, Theme.Text);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg);

            // subtle warm glow top-left
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(-S(120), -S(160), S(420), S(360));
                using (PathGradientBrush pb = new PathGradientBrush(gp))
                {
                    pb.CenterColor = Color.FromArgb(s.Enabled ? 34 : 12, Theme.Accent2);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, Theme.Accent2) };
                    g.FillPath(pb, gp);
                }
            }

            // header
            Theme.DrawMoon(g, new RectangleF(S(P), S(Y_HEADER), S(26), S(26)), s.Enabled ? Theme.Accent : Theme.Muted, Theme.Bg, s.Enabled);
            Txt(g, "Night Dimmer", fTitle, P + 38, Y_HEADER - 3, Theme.Text);
            Size tsz = TextRenderer.MeasureText(g, "Night Dimmer", fTitle, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "v" + About.Version, fHint, new Point(S(P + 38) + tsz.Width + S(8), S(Y_HEADER + 6)), Theme.Muted, TextFormatFlags.NoPadding);
            Txt(g, "Warm, click-through screen filter", fSub, P + 38, Y_SUB - 6, Theme.Muted);

            // master row
            Txt(g, "Filter", fLabel, P, Y_MASTER, Theme.Text);
            string status = s.Enabled
                ? "On  ·  " + s.Dim + "% dim  ·  " + s.Warmth + "% warm  ·  " + s.Blue + "% blue cut"
                : "Off";
            Txt(g, status, fSub, P, Y_MASTER + 22, s.Enabled ? Theme.Accent : Theme.Muted);

            Section(g, "Dim", s.Dim + "%", Y_DIM);
            Section(g, "Warmth", s.Warmth + "%", Y_WARM);
            Section(g, "Blue light filter", s.Blue + "%", Y_BLUE);

            if (unlockLink.Visible)
                Txt(g, "Windows caps display gamma at 50%", fHint, P, Y_UNLOCK + 3, Theme.Muted);

            using (Pen p = new Pen(Theme.Border)) g.DrawLine(p, S(P), S(Y_DIV), S(W - P), S(Y_DIV));

            Txt(g, "Start with Windows", fSub, P, Y_START + 3, Theme.Text);
            Txt(g, "Screen off powers monitors down", fSub, P, Y_POWER + 3, Theme.Text);
            Txt(g, s.PowerOff ? "turn off if your PC asks you to sign in on wake" : "off: backlight to minimum + black overlay", fHint, P, Y_POWER + 22, Theme.Muted);

            Txt(g, "Ctrl+Alt+D  panel   ·   0  on / off   ·   B  screen off", fHint, P, Y_FOOT - 2, Theme.Muted);
            Txt(g, "Ctrl+Alt+ − / =  dim   ·   9  warmth   ·   8  blue", fHint, P, Y_FOOT + 13, Theme.Muted);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fade.Dispose(); fTitle.Dispose(); fSub.Dispose(); fLabel.Dispose(); fSection.Dispose(); fValue.Dispose(); fHint.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // =====================================================================
    //  Tray application
    // =====================================================================

    class TrayApp : ApplicationContext
    {
        public const int MAX_DIM = 90;
        const int STEP = 5;
        const int HK_DARKER = 1, HK_BRIGHTER = 2, HK_TOGGLE = 3, HK_WARMTH = 4, HK_PANEL = 5, HK_BLUE = 6, HK_BLACKOUT = 7;
        const int HK_ALT = 10; // alternate bindings use id + HK_ALT
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        readonly Settings s;
        readonly List<OverlayForm> overlays = new List<OverlayForm>();
        readonly NotifyIcon tray;
        readonly HotkeyWindow hk;
        readonly System.Windows.Forms.Timer topTimer;
        readonly Icon iconOn, iconOff;
        readonly Gamma gamma;
        readonly InputWatch input = new InputWatch();
        readonly Backlight backlight = new Backlight();
        DateTime powerOffSentAt = DateTime.MinValue;
        bool blackout;

        PanelForm panel;

        public TrayApp(List<KeyValuePair<Cmd, int>> startupCmds)
        {
            Log.W("---- start  screens=" + Screen.AllScreens.Length + "  exe=" + Application.ExecutablePath);
            s = Settings.Load();
            iconOn = MakeIcon(true);
            iconOff = MakeIcon(false);
            gamma = new Gamma();

            // Never leave the display darkened if we die or the user signs out.
            AppDomain.CurrentDomain.ProcessExit += delegate { gamma.Restore(); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs e)
            {
                Log.W("FATAL " + e.ExceptionObject); gamma.Restore();
            };
            Application.ThreadException += delegate(object o, System.Threading.ThreadExceptionEventArgs e)
            {
                Log.W("ERROR " + e.Exception); gamma.Restore();
            };
            SystemEvents.SessionEnding += delegate { gamma.Restore(); backlight.Restore(); };
            SystemEvents.SessionSwitch += delegate(object o, SessionSwitchEventArgs e)
            {
                // Managed / Modern Standby PCs lock the moment the display powers off. If that happens
                // right after our power-off, stop using DPMS on this machine and say so.
                if (e.Reason == SessionSwitchReason.SessionLock && blackout && s.PowerOff &&
                    (DateTime.UtcNow - powerOffSentAt).TotalSeconds < 15)
                {
                    s.PowerOff = false;
                    s.Save();
                    Log.W("lock screen followed power-off -> PowerOff disabled");
                    EndBlackout();
                    tray.ShowBalloonTip(10000, "Night Dimmer",
                        "This PC locks when the display powers off (a sign-in policy). Screen off will now dim the " +
                        "backlight and black out instead. You can re-enable power-off in the panel.", ToolTipIcon.Info);
                }
            };
            backlight.RecoverIfNeeded();

            tray = new NotifyIcon();
            tray.MouseClick += delegate(object o, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right) ShowPanel();
            };
            tray.Visible = true;

            hk = new HotkeyWindow();
            hk.Pressed += OnHotkey;
            hk.Command += OnCommand;
            uint ca = Native.MOD_CONTROL | Native.MOD_ALT;
            bool hotkeysOk = true;
            // Primary set, plus alternates in case another app owns the primaries.
            hotkeysOk &= Reg(HK_DARKER,   ca, Keys.OemMinus);  Reg(HK_DARKER + HK_ALT,   ca, Keys.PageDown);
            hotkeysOk &= Reg(HK_BRIGHTER, ca, Keys.Oemplus);   Reg(HK_BRIGHTER + HK_ALT, ca, Keys.PageUp);
            hotkeysOk &= Reg(HK_TOGGLE,   ca, Keys.D0);        Reg(HK_TOGGLE + HK_ALT,   ca, Keys.End);
            hotkeysOk &= Reg(HK_WARMTH,   ca, Keys.D9);        Reg(HK_WARMTH + HK_ALT,   ca, Keys.Home);
            hotkeysOk &= Reg(HK_PANEL,    ca, Keys.D);
            hotkeysOk &= Reg(HK_BLUE,     ca, Keys.D8);
            hotkeysOk &= Reg(HK_BLACKOUT, ca, Keys.B);
            input.Woken += delegate(string why) { Log.W("wake: " + why); EndBlackout(); };

            SystemEvents.DisplaySettingsChanged += delegate { gamma.Refresh(); BuildOverlays(); };
            SystemEvents.PowerModeChanged += delegate(object o, PowerModeChangedEventArgs e)
            {
                if (e.Mode == PowerModes.Resume) Apply(); // drivers reset ramps on wake
            };

            // Other topmost windows can slip above the overlay, and apps/drivers can reset gamma; re-assert both.
            topTimer = new System.Windows.Forms.Timer();
            topTimer.Interval = 1500;
            topTimer.Tick += delegate
            {
                foreach (OverlayForm f in overlays) if (f.Visible) f.ReassertTopmost();
                if (s.Enabled) gamma.Reapply();
            };
            topTimer.Start();

            foreach (KeyValuePair<Cmd, int> c in startupCmds) ApplyCommand(c.Key, c.Value);
            BuildOverlays();
            PromoteTrayIcon();

            if (s.FirstRun)
            {
                s.FirstRun = false;
                s.Save();
                tray.ShowBalloonTip(8000, "Night Dimmer is running",
                    "Ctrl+Alt+D opens the control panel.\n" +
                    "Ctrl+Alt+-  darker    Ctrl+Alt+=  brighter    Ctrl+Alt+0  on/off",
                    ToolTipIcon.Info);
            }
            else if (!hotkeysOk)
            {
                tray.ShowBalloonTip(5000, "Night Dimmer",
                    "Some hotkeys are taken by another app - see %APPDATA%\\NightDimmer.log.",
                    ToolTipIcon.Warning);
            }
        }

        bool Reg(int id, uint mods, Keys key)
        {
            bool ok = hk.Register(id, mods, key);
            Log.W("hotkey Ctrl+Alt+" + key + " -> " + (ok ? "ok" : "FAILED (already registered by another app)"));
            return ok;
        }

        // Windows 11 hides new tray icons in the overflow. Flip IsPromoted so ours stays visible.
        void PromoteTrayIcon()
        {
            try
            {
                string exe = Application.ExecutablePath;
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) { Log.W("NotifyIconSettings key missing - cannot promote icon"); return; }
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using (RegistryKey k = root.OpenSubKey(name, true))
                        {
                            string path = k == null ? null : k.GetValue("ExecutablePath") as string;
                            if (path != null && string.Equals(path, exe, StringComparison.OrdinalIgnoreCase))
                            {
                                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                Log.W("tray icon promoted (" + name + ")");
                                return;
                            }
                        }
                    }
                    Log.W("tray icon entry not found in NotifyIconSettings yet");
                }
            }
            catch (Exception ex) { Log.W("promote failed: " + ex.Message); }
        }

        // ---- overlays -------------------------------------------------------

        void BuildOverlays()
        {
            foreach (OverlayForm f in overlays) f.Close();
            overlays.Clear();
            foreach (Screen sc in Screen.AllScreens)
            {
                OverlayForm f = new OverlayForm(sc.Bounds);
                f.Clicked += delegate { Log.W("wake: overlay click"); EndBlackout(); };
                overlays.Add(f);
            }
            if (blackout) { blackout = false; StartBlackout(); } else Apply();
        }

        // ---- blackout / screen off ----------------------------------------------
        //
        // Overlay goes black immediately (no bright flash, and a fallback where DPMS is not
        // supported, e.g. Remote Desktop), then the monitors are told to power off. Any key,
        // mouse button or mouse movement wakes: the hooks lift the overlay and we switch the
        // monitors back on explicitly, since a swallowed key may not count as wake input.

        void StartBlackout()
        {
            if (blackout) return;
            blackout = true;
            Log.W("blackout on");
            if (panel != null && !panel.IsDisposed) panel.Close();
            foreach (OverlayForm f in overlays) f.SetBlackout(true);
            if (!input.Start(1500))
                tray.ShowBalloonTip(4000, "Night Dimmer", "Couldn't watch the keyboard - move or click the mouse, or press Ctrl+Alt+B, to wake the screen.", ToolTipIcon.Warning);
            tray.Text = "Night Dimmer: screen off - any key or mouse wakes it";
            // Give the click/keystroke that started this a moment to settle, or the display wakes straight back up.
            System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
            t.Interval = 600;
            t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                if (!blackout) return;
                backlight.DimAsync();
                if (s.PowerOff) { powerOffSentAt = DateTime.UtcNow; MonitorPower(false); }
            };
            t.Start();
        }

        void EndBlackout()
        {
            input.Stop();
            if (!blackout) return;
            blackout = false;
            Log.W("blackout off");
            if (s.PowerOff) MonitorPower(true);
            backlight.Restore();
            foreach (OverlayForm f in overlays) f.SetBlackout(false);
            Apply();
        }

        void ToggleBlackout() { if (blackout) EndBlackout(); else StartBlackout(); }

        void MonitorPower(bool on)
        {
            // WM_SYSCOMMAND / SC_MONITORPOWER handled by DefWindowProc of any window we own.
            Native.SendMessage(hk.Handle, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_MONITORPOWER), new IntPtr(on ? -1 : 2));
        }

        static Color TintFor(int warmth)
        {
            // Dark amber. Blended at <=90% opacity this cuts blue without washing out.
            double w = warmth / 100.0;
            return Color.FromArgb((int)(w * 110), (int)(w * 55), 0);
        }

        void Apply()
        {
            if (blackout) { s.Save(); return; }        // overlays are busy being black; settings still stick
            double target = 1 - s.Dim / 100.0;        // brightness we want
            double w = s.Warmth / 100.0;
            double reached = 1;                        // brightness gamma delivered
            Color tint = Color.Black;

            if (s.Enabled)
            {
                reached = gamma.Apply(target, w, s.Blue / 100.0);
                if (!gamma.Supported) tint = TintFor(s.Warmth); // no gamma here: overlay carries warmth (additive)
            }
            else gamma.Restore();

            // Overlay makes up whatever gamma could not: target = reached * (1 - opacity)
            double opacity = reached > 0 ? 1 - target / reached : 0;
            if (opacity < 0.005) opacity = 0;
            bool show = s.Enabled && opacity > 0;

            foreach (OverlayForm f in overlays)
            {
                f.BackColor = tint;
                f.Opacity = Math.Min(0.95, opacity);
                if (show)
                {
                    if (!f.Visible) f.Show();
                    f.ReassertTopmost();
                }
                else if (f.Visible) f.Hide();
            }
            tray.Icon = s.Enabled ? iconOn : iconOff;
            tray.Text = s.Enabled
                ? "Night Dimmer: " + s.Dim + "% dim, " + s.Warmth + "% warm, " + s.Blue + "% blue cut"
                : "Night Dimmer: off";
            if (panel != null && !panel.IsDisposed) panel.Sync();
            s.Save();
        }

        void Toggle()
        {
            s.Enabled = !s.Enabled;
            if (s.Enabled && s.Dim == 0) s.Dim = 30; // "on" should visibly do something
            Apply();
        }

        void OnHotkey(int id)
        {
            if (id > HK_ALT) id -= HK_ALT;
            if (blackout && id != HK_BLACKOUT) { Log.W("wake: hotkey " + id); EndBlackout(); return; } // any hotkey wakes, nothing more
            switch (id)
            {
                case HK_BLACKOUT: ToggleBlackout(); return;
                case HK_PANEL:    ShowPanel(); return;
                case HK_DARKER:   s.Dim = Math.Min(MAX_DIM, s.Dim + STEP); s.Enabled = true; break;
                case HK_BRIGHTER: s.Dim = Math.Max(0, s.Dim - STEP); break;
                case HK_TOGGLE:   Toggle(); return;
                case HK_WARMTH:   s.Warmth = s.Warmth >= 100 ? 0 : Math.Min(100, (s.Warmth / 25 + 1) * 25); break;
                case HK_BLUE:     s.Blue = s.Blue >= 100 ? 0 : Math.Min(100, (s.Blue / 25 + 1) * 25); break;
            }
            Apply();
        }

        void OnCommand(Cmd cmd, int val)
        {
            Log.W("command " + cmd + " " + val);
            ApplyCommand(cmd, val);
            if (cmd != Cmd.Exit) Apply();
        }

        void ApplyCommand(Cmd cmd, int val)
        {
            switch (cmd)
            {
                case Cmd.Dim:    s.Dim = Settings.Clamp(val, 0, MAX_DIM); s.Enabled = s.Dim > 0; break;
                case Cmd.Warmth: s.Warmth = Settings.Clamp(val, 0, 100); break;
                case Cmd.Blue:   s.Blue = Settings.Clamp(val, 0, 100); break;
                case Cmd.Toggle: s.Enabled = !s.Enabled; if (s.Enabled && s.Dim == 0) s.Dim = 30; break;
                case Cmd.On:     s.Enabled = true; if (s.Dim == 0) s.Dim = 30; break;
                case Cmd.Off:    s.Enabled = false; break;
                case Cmd.Panel:  ShowPanel(); break;
                case Cmd.Blackout: ToggleBlackout(); break;
                case Cmd.Exit:   ExitApp(); break;
            }
        }

        void ShowPanel()
        {
            if (panel == null || panel.IsDisposed)
            {
                panel = new PanelForm(s, MAX_DIM, delegate { Apply(); }, delegate { ExitApp(); },
                    delegate { return IsStartup(); }, delegate(bool on) { SetStartup(on); },
                    delegate { return gamma.Capped; }, delegate { UnlockGammaRange(); }, delegate { ToggleBlackout(); });
                panel.FormClosed += delegate { panel = null; };
            }
            panel.Sync();
            if (!panel.Visible) panel.Show();
            panel.Activate();
            Native.SetForegroundWindow(panel.Handle);
            // keep the filter above the panel so the sliders preview live
            foreach (OverlayForm f in overlays) if (f.Visible) f.ReassertTopmost();
        }

        // Windows caps gamma at 50% brightness; GdiIcmGammaRange=256 lifts the cap so the whole
        // dim range (and therefore Alt+Tab / Start) goes through gamma. HKLM => elevation + sign-out.
        void UnlockGammaRange()
        {
            DialogResult r = MessageBox.Show(
                "Windows limits display gamma to 50% brightness. Below that, Night Dimmer uses an overlay, " +
                "which shell UI like Alt+Tab and the Start menu can appear above.\n\n" +
                "Unlocking sets GdiIcmGammaRange=256 in HKLM (asks for admin). Sign out and back in afterwards, " +
                "and dimming up to 90% will apply to everything.\n\nContinue?",
                "Night Dimmer", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (r != DialogResult.Yes) return;
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo("reg.exe",
                    "add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM\" /v GdiIcmGammaRange /t REG_DWORD /d 256 /f");
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
                System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit();
                Log.W("GdiIcmGammaRange set, exit=" + p.ExitCode);
                MessageBox.Show(p.ExitCode == 0
                    ? "Done. Sign out and back in (or restart) for the change to take effect."
                    : "reg.exe returned " + p.ExitCode + ".", "Night Dimmer");
            }
            catch (Exception ex) { Log.W("unlock cancelled/failed: " + ex.Message); }
        }

        // ---- startup ----------------------------------------------------------

        static bool IsStartup()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("NightDimmer") != null;
        }

        static void SetStartup(bool on)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("NightDimmer", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("NightDimmer", false);
            }
        }

        // ---- misc -------------------------------------------------------------

        static Icon MakeIcon(bool on)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                Color c = on ? Theme.Accent : Color.FromArgb(150, 150, 150);
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, 2, 2, 28, 28);
                g.CompositingMode = CompositingMode.SourceCopy; // punch out a crescent
                using (SolidBrush t = new SolidBrush(Color.Transparent)) g.FillEllipse(t, 11, -3, 28, 28);
                IntPtr h = bmp.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return ic;
            }
        }

        void ExitApp()
        {
            Log.W("exit");
            topTimer.Stop();
            input.Dispose();
            gamma.Restore();
            backlight.Restore();
            if (panel != null && !panel.IsDisposed) panel.Close();
            foreach (OverlayForm f in overlays) f.Close();
            overlays.Clear();
            tray.Visible = false;
            tray.Dispose();
            hk.Dispose();
            ExitThread();
        }
    }

    static class Program
    {
        static List<KeyValuePair<Cmd, int>> ParseArgs(string[] args)
        {
            List<KeyValuePair<Cmd, int>> cmds = new List<KeyValuePair<Cmd, int>>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].TrimStart('-', '/').ToLowerInvariant();
                int v = 0;
                bool hasVal = i + 1 < args.Length && int.TryParse(args[i + 1], out v);
                switch (a)
                {
                    case "dim":    if (hasVal) { cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Dim, v)); i++; } break;
                    case "warmth": if (hasVal) { cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Warmth, v)); i++; } break;
                    case "blue":   if (hasVal) { cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Blue, v)); i++; } break;
                    case "on":     cmds.Add(new KeyValuePair<Cmd, int>(Cmd.On, 0)); break;
                    case "off":    cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Off, 0)); break;
                    case "toggle": cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Toggle, 0)); break;
                    case "panel":  cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Panel, 0)); break;
                    case "blackout": cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Blackout, 0)); break;
                    case "exit":   cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Exit, 0)); break;
                }
            }
            return cmds;
        }

        [STAThread]
        static void Main(string[] args)
        {
            List<KeyValuePair<Cmd, int>> cmds = ParseArgs(args);

            // Already running? Forward the commands to that instance and quit.
            IntPtr existing = Native.FindWindow(null, Native.CTL_TITLE);
            if (existing != IntPtr.Zero)
            {
                if (cmds.Count == 0) cmds.Add(new KeyValuePair<Cmd, int>(Cmd.Panel, 0)); // plain re-launch opens the panel
                foreach (KeyValuePair<Cmd, int> c in cmds)
                    Native.SendMessage(existing, Native.WM_APP_CMD, new IntPtr((int)c.Key), new IntPtr(c.Value));
                return;
            }
            if (cmds.Exists(delegate(KeyValuePair<Cmd, int> c) { return c.Key == Cmd.Exit; })) return;

            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp(cmds));
        }
    }
}
