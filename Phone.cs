using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ScrcpyZ;

/// <summary>Komande za telefon preko adb-a: buđenje i otključavanje.</summary>
public static class Phone
{
    static readonly Regex LockedTrue = new(@"(mDreamingLockscreen|mShowingLockscreen|mKeyguardShowing|isKeyguardShowing)=true");
    static readonly Regex LockedFalse = new(@"(mDreamingLockscreen|mShowingLockscreen|mKeyguardShowing|isKeyguardShowing)=false");

    static Task<string> Sh(string adb, string serial, string cmd, int timeout = 8000) =>
        Proc.RunAsync(adb, $"-s {serial} shell {cmd}", timeout);

    /// <summary>true = zaključan, false = otključan, null = ne može da se utvrdi.</summary>
    public static async Task<bool?> IsLockedAsync(string adb, string serial)
    {
        var w = await Sh(adb, serial, "dumpsys window");
        if (LockedTrue.IsMatch(w)) return true;
        if (LockedFalse.IsMatch(w)) return false;
        return null;
    }

    public static async Task<(int w, int h)> ScreenSizeAsync(string adb, string serial)
    {
        var r = await Sh(adb, serial, "wm size");
        var ms = Regex.Matches(r, @"(\d+)x(\d+)");
        if (ms.Count > 0)
        {
            var m = ms[^1]; // "Override size" ima prednost ako postoji
            return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }
        return (1080, 2400);
    }

    /// <summary>Budi telefon i prevlači preko zaključanog ekrana (da se pojave PIN polje ili tačke šablona).</summary>
    public static async Task<bool> WakeAndSwipeAsync(string adb, string serial)
    {
        await Sh(adb, serial, "input keyevent KEYCODE_WAKEUP");
        await Task.Delay(500);
        if (await IsLockedAsync(adb, serial) == false) return false; // već otključan

        var (w, h) = await ScreenSizeAsync(adb, serial);
        int x = w / 2, y1 = (int)(h * 0.85), y2 = (int)(h * 0.30);
        await Sh(adb, serial, $"input swipe {x} {y1} {x} {y2} 300");
        await Task.Delay(700);
        return true;
    }

    /// <summary>Koordinate tačke šablona (1–9) na ekranu telefona.</summary>
    public static (int x, int y) DotPosition(int dot, int w, int h, double centerX, double centerY,
        double spacing, double spacingY = 0)
    {
        int r = (dot - 1) / 3, c = (dot - 1) % 3;
        double stepX = w * spacing;
        double stepY = spacingY > 0 ? h * spacingY : stepX;
        return ((int)(w * centerX + (c - 1) * stepX), (int)(h * centerY + (r - 1) * stepY));
    }

    public record Grid(double CenterX, double CenterY, double Spacing, double SpacingY);

    static readonly Regex PatternNode = new(
        @"<node\b[^>]*?(?:resource-id=""[^""]*lockPatternView[^""]*""|class=""[^""]*LockPatternView[^""]*"")[^>]*>",
        RegexOptions.IgnoreCase);
    static readonly Regex Bounds = new(@"bounds=""\[(\d+),(\d+)\]\[(\d+),(\d+)\]""");

    /// <summary>
    /// Pronalazi stvarni položaj polja sa šablonom preko uiautomator-a (LockPatternView).
    /// Telefon mora da prikazuje ekran sa tačkama. Vraća null ako ne uspe.
    /// </summary>
    public static async Task<Grid?> FindPatternGridAsync(string adb, string serial, int w, int h)
    {
        var xml = await Proc.RunAsync(adb, $"-s {serial} exec-out uiautomator dump /dev/tty", 20000);
        if (!xml.Contains("<hierarchy"))
        {
            await Sh(adb, serial, "uiautomator dump /sdcard/scrcpyz_ui.xml", 20000);
            xml = await Sh(adb, serial, "cat /sdcard/scrcpyz_ui.xml");
            await Sh(adb, serial, "rm /sdcard/scrcpyz_ui.xml");
        }
        return ParseGrid(xml, w, h);
    }

    public static Grid? ParseGrid(string xml, int w, int h)
    {
        var node = PatternNode.Match(xml);
        if (!node.Success) return null;
        var b = Bounds.Match(node.Value);
        if (!b.Success) return null;

        double l = double.Parse(b.Groups[1].Value), t = double.Parse(b.Groups[2].Value);
        double r = double.Parse(b.Groups[3].Value), bo = double.Parse(b.Groups[4].Value);
        if (r - l < 50 || bo - t < 50 || w <= 0 || h <= 0) return null;

        // tačke su u centrima ćelija mreže 3×3 preko celog polja
        return new Grid((l + r) / 2 / w, (t + bo) / 2 / h, (r - l) / 3.0 / w, (bo - t) / 3.0 / h);
    }

    /// <summary>Crta šablon jednim neprekidnim potezom (DOWN → MOVE … → UP).</summary>
    public static async Task DrawPatternAsync(string adb, string serial, string pattern, int w, int h,
        double centerX, double centerY, double spacing, double spacingY = 0)
    {
        var dots = pattern.Where(ch => ch is >= '1' and <= '9').Select(ch => ch - '0').ToList();
        if (dots.Count < 2) return;

        var cmds = new List<string>();
        for (int i = 0; i < dots.Count; i++)
        {
            var (x, y) = DotPosition(dots[i], w, h, centerX, centerY, spacing, spacingY);
            if (i == 0)
            {
                cmds.Add($"input motionevent DOWN {x} {y}");
                cmds.Add($"input motionevent MOVE {x} {y + 2}");
            }
            else
            {
                // jedna međutačka da bi potez bio gladak
                var (px, py) = DotPosition(dots[i - 1], w, h, centerX, centerY, spacing, spacingY);
                cmds.Add($"input motionevent MOVE {(px + x) / 2} {(py + y) / 2}");
                cmds.Add($"input motionevent MOVE {x} {y}");
            }
        }
        var (lx, ly) = DotPosition(dots[^1], w, h, centerX, centerY, spacing, spacingY);
        cmds.Add($"input motionevent UP {lx} {ly}");

        // sve u jednom pozivu, da potez ne bude prekinut
        await Proc.RunAsync(adb, $"-s {serial} shell \"{string.Join("; ", cmds)}\"", 30000);
    }

    /// <summary>Kompletno otključavanje prema podešavanjima (PIN/lozinka ili šablon).</summary>
    public static async Task<string> UnlockAsync(string adb, string serial, Settings s)
    {
        if (!await WakeAndSwipeAsync(adb, serial))
            return "Telefon je već otključan.";

        bool pattern = s.LockType == "pattern";
        string secret = pattern ? s.Pattern : s.Pin;

        string note = "";
        if (pattern && secret.Length >= 2)
        {
            var (w, h) = await ScreenSizeAsync(adb, serial);
            var grid = await FindPatternGridAsync(adb, serial, w, h);
            if (grid != null)
            {
                s.GridCenterX = grid.CenterX; s.GridCenterY = grid.CenterY;
                s.GridSpacing = grid.Spacing; s.GridSpacingY = grid.SpacingY;
                s.Save();
            }
            else note = " (tačke nisu pronađene automatski — korišćen je sačuvan položaj)";
            await DrawPatternAsync(adb, serial, secret, w, h, s.GridCenterX, s.GridCenterY, s.GridSpacing, s.GridSpacingY);
            await Task.Delay(900);
        }
        else if (!pattern && secret.Length > 0)
        {
            await Sh(adb, serial, "input text " + ShellEscape(secret));
            await Task.Delay(250);
            await Sh(adb, serial, "input keyevent KEYCODE_ENTER");
            await Task.Delay(900);
        }

        return await IsLockedAsync(adb, serial) switch
        {
            false => "Telefon je otključan.",
            true when pattern && secret.Length > 0 => "Telefon je i dalje zaključan" + note + ". Otvori „Podesi šablon…“ i klikni „Probaj na telefonu“.",
            true => secret.Length == 0
                ? (pattern ? "Telefon je i dalje zaključan — podesi šablon u opcijama." : "Telefon je i dalje zaključan — upiši PIN u opcijama.")
                : (pattern ? "Telefon je i dalje zaključan — proveri šablon i položaj tačaka („Podesi šablon…“)." : "Telefon je i dalje zaključan — proveri PIN."),
            _ => "Komande za otključavanje su poslate."
        };
    }

    public const string KeepAwakeTimeout = "2147483647";

    public static async Task<string?> GetScreenTimeoutAsync(string adb, string serial)
    {
        var v = (await Sh(adb, serial, "settings get system screen_off_timeout")).Trim();
        return Regex.IsMatch(v, @"^\d+$") ? v : null;
    }

    public static Task SetScreenTimeoutAsync(string adb, string serial, string value) =>
        Sh(adb, serial, $"settings put system screen_off_timeout {value}");

    /// <summary>Uključuje/isključuje prikaz dodira na telefonu (Developer options → Pointer location).</summary>
    public static Task SetPointerLocationAsync(string adb, string serial, bool on) =>
        Sh(adb, serial, $"settings put system pointer_location {(on ? 1 : 0)}");

    // adb shell prosleđuje tekst shell-u na telefonu: razmak = %s, ostalo pod jednostruke navodnike
    static string ShellEscape(string s)
    {
        var t = s.Replace(" ", "%s").Replace("'", "'\\''");
        return "'" + t + "'";
    }
}

/// <summary>Šalje prečicu Alt+O prozoru scrcpy-ja (gašenje ekrana telefona uz nastavak prikaza).</summary>
public static class ScrcpyKeys
{
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    const byte VK_LMENU = 0xA4, VK_O = 0x4F;
    const uint KEYUP = 0x0002;

    public static async Task<IntPtr> WaitForWindowAsync(Process p, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                if (p.HasExited) return IntPtr.Zero;
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            catch { return IntPtr.Zero; }
            await Task.Delay(250);
        }
        return IntPtr.Zero;
    }

    public static async Task BringToFrontAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
        SetForegroundWindow(hwnd);
        await Task.CompletedTask;
    }

    public static async Task<bool> SendAltOAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9); // SW_RESTORE
        SetForegroundWindow(hwnd);
        await Task.Delay(200);
        // tastatura ide aktivnom prozoru — ako scrcpy nije u fokusu, Alt+O bi otišao drugom programu
        if (GetForegroundWindow() != hwnd) return false;
        keybd_event(VK_LMENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_O, 0, 0, UIntPtr.Zero);
        await Task.Delay(30);
        keybd_event(VK_O, 0, KEYUP, UIntPtr.Zero);
        keybd_event(VK_LMENU, 0, KEYUP, UIntPtr.Zero);
        return true;
    }
}

/// <summary>Windows DPAPI (CurrentUser) bez dodatnih NuGet paketa.</summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob dataIn, string? desc, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr desc, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr h);

    const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static string Protect(string plain)
    {
        try { return Convert.ToBase64String(Run(Encoding.UTF8.GetBytes(plain), true)); }
        catch { return ""; }
    }

    public static string Unprotect(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return "";
        try { return Encoding.UTF8.GetString(Run(Convert.FromBase64String(b64), false)); }
        catch { return ""; }
    }

    static byte[] Run(byte[] data, bool protect)
    {
        var h = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { cbData = data.Length, pbData = h.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "ScrcpyZ", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) throw new InvalidOperationException("DPAPI");
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally { h.Free(); }
    }
}
