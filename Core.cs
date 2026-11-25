using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScrcpyZ;

/// <summary>Jedan telefon u listi (povezan preko USB/Wi-Fi ili samo vidljiv na mreži).</summary>
public class DeviceInfo
{
    public string Key { get; set; } = "";          // stabilan ključ: serijski broj telefona
    public string Name { get; set; } = "";
    public string? Serial { get; set; }            // adb serial ako je povezan
    public string? Address { get; set; }           // IP:port sa mreže (mDNS)
    public bool IsUsb { get; set; }
    public string State { get; set; } = "available";
    public bool IsFavorite { get; set; }

    public string Star => IsFavorite ? "★" : "";
    public string Connection => IsUsb ? "USB" : "Wi-Fi";
    public string AddressText => Address ?? Serial ?? "";
    public string StatusText => State switch
    {
        "device" => "Povezan",
        "available" => "Dostupan",
        "unauthorized" => "Čeka dozvolu na telefonu",
        "offline" => "Offline",
        _ => State
    };
    public string Signature => $"{Key}|{Name}|{Serial}|{Address}|{IsUsb}|{State}|{IsFavorite}";
}

public class Settings
{
    public string? ScrcpyFolder { get; set; }
    public string? FavoriteKey { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool AutoRefresh { get; set; } = true;
    public bool ScreenOff { get; set; }
    public bool StayAwake { get; set; } = true;
    public bool NoAudio { get; set; }
    public bool OnTop { get; set; }
    public bool Fullscreen { get; set; }
    public int MaxSize { get; set; }
    public int Bitrate { get; set; } = 8;
    public string Extra { get; set; } = "";
    public bool AutoUnlock { get; set; }
    public bool TrayAfterConnect { get; set; } = true; // posle pokretanja scrcpy-ja sakrij ScrcpyZ u tray
    public bool FixedPort { get; set; } = true;        // prebaci telefon na port 5555 i traži ga po mreži (bez mDNS-a)
    public string? LastAddress { get; set; }           // poslednja adresa omiljenog telefona (IP:port)
    public string LockType { get; set; } = "pin";   // "pin" ili "pattern"
    public string? PatternProtected { get; set; }    // šablon kao niz tačaka 1-9, šifrovan
    public double GridCenterX { get; set; } = 0.50;  // srednja kolona tačaka, udeo širine ekrana
    public double GridCenterY { get; set; } = 0.72;  // srednji red tačaka, udeo visine ekrana
    public double GridSpacing { get; set; } = 0.25;  // razmak tačaka, udeo širine ekrana
    public double GridSpacingY { get; set; }         // vertikalni razmak, udeo visine (0 = isto kao horizontalni)

    [System.Text.Json.Serialization.JsonIgnore]
    public string Pattern
    {
        get => Dpapi.Unprotect(PatternProtected);
        set => PatternProtected = string.IsNullOrEmpty(value) ? null : Dpapi.Protect(value);
    }
    public string? PinProtected { get; set; }       // PIN šifrovan DPAPI-jem (samo ovaj Windows nalog)
    public Dictionary<string, string> Names { get; set; } = new();

    // Originalno vreme gašenja ekrana telefona dok ga ScrcpyZ drži budnim (vraća se po zatvaranju scrcpy-ja)
    public string? SavedTimeoutKey { get; set; }
    public string? SavedTimeoutValue { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string Pin
    {
        get => Dpapi.Unprotect(PinProtected);
        set => PinProtected = string.IsNullOrEmpty(value) ? null : Dpapi.Protect(value);
    }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrcpyZ", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public static class Proc
{
    /// <summary>Pokreće komandu bez prozora i vraća stdout + stderr.</summary>
    public static async Task<string> RunAsync(string exe, string args, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        try { p.Start(); }
        catch (Exception ex) { return "ERROR: " + ex.Message; }

        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }

        await Task.WhenAny(Task.WhenAll(o, e), Task.Delay(2000));
        return (o.IsCompletedSuccessfully ? o.Result : "") + (e.IsCompletedSuccessfully ? e.Result : "");
    }

    /// <summary>Pokreće komandu i vraća binarni stdout (npr. screencap).</summary>
    public static async Task<byte[]> RunBytesAsync(string exe, string args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = new Process { StartInfo = psi };
        try { p.Start(); } catch { return []; }
        using var ms = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        _ = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
        await Task.WhenAny(copy, Task.Delay(2000));
        return ms.ToArray();
    }

    /// <summary>adb server se pokreće bez preusmerenog izlaza, da ne bi zadržao pipe otvorenim.</summary>
    public static async Task StartAdbServerAsync(string adb)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(adb, "start-server")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p != null)
            {
                using var cts = new CancellationTokenSource(15000);
                try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
            }
        }
        catch { }
    }

    public static string? FindScrcpyDir(string? customFolder)
    {
        var dirs = new List<string> { AppContext.BaseDirectory };
        if (!string.IsNullOrWhiteSpace(customFolder)) dirs.Add(customFolder);
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var d in dirs)
        {
            try { if (File.Exists(Path.Combine(d, "scrcpy.exe"))) return Path.GetFullPath(d); }
            catch { }
        }
        return null;
    }
}

public static class DeviceScanner
{
    static readonly Regex DevLine = new(@"^(\S+)\s+(device|unauthorized|offline|authorizing|connecting)\b(.*)$");
    static readonly Regex MdnsLine = new(@"^(\S+)\s+_adb-tls-connect\._tcp\.?\s+(\d+\.\d+\.\d+\.\d+:\d+)");
    static readonly Regex IpSerial = new(@"^\d+\.\d+\.\d+\.\d+:\d+$");
    static readonly Regex Instance = new(@"^adb-(.+)-[^-]+$");
    static readonly Dictionary<string, string> HwCache = new();

    /// <summary>"adb-R58M12345-AbCdEf(._adb-tls-connect._tcp)" → "R58M12345"</summary>
    public static bool IsIpKey(string key) => IpSerial.IsMatch(key);

    public static string? HwSerial(string name)
    {
        int i = name.IndexOf("._adb-tls-connect", StringComparison.Ordinal);
        if (i >= 0) name = name[..i];
        var m = Instance.Match(name);
        return m.Success ? m.Groups[1].Value : null;
    }

    static readonly Dictionary<string, DateTime> Suspect = new();

    /// <summary>Beleži problem sa vezom; true kad problem traje duže od zadatog vremena.</summary>
    static bool Strike(string serial, TimeSpan grace)
    {
        var now = DateTime.UtcNow;
        if (!Suspect.TryGetValue(serial, out var since)) { Suspect[serial] = now; return false; }
        return now - since >= grace;
    }

    static async Task Disconnect(string adb, string serial)
    {
        Suspect.Remove(serial);
        HwCache.Remove(serial);
        try { await Proc.RunAsync(adb, $"disconnect {serial}", 5000); } catch { }
    }

    public static async Task<List<DeviceInfo>> ScanAsync(string adb, Settings s)
    {
        var result = new List<DeviceInfo>();

        // 1) Već povezani uređaji (USB i bežični)
        var devOut = await Proc.RunAsync(adb, "devices -l");
        if (!devOut.Contains("List of devices"))
        {
            // adb server je ugašen (npr. "adb kill-server"): ako ga pokrene ova komanda,
            // server zadrži pipe i izlaz se izgubi. Pokreni ga zasebno i probaj ponovo.
            await Proc.StartAdbServerAsync(adb);
            devOut = await Proc.RunAsync(adb, "devices -l");
        }
        var seen = new HashSet<string>();
        foreach (var raw in devOut.Split('\n').Skip(1))
        {
            var m = DevLine.Match(raw.Trim());
            if (!m.Success) continue;

            string serial = m.Groups[1].Value, state = m.Groups[2].Value, rest = m.Groups[3].Value;
            bool isTls = serial.Contains("._adb-tls-connect");
            bool isIp = IpSerial.IsMatch(serial);
            bool usb = !isTls && !isIp;

            seen.Add(serial);
            // Stara IP veza koja više ne radi (telefon dobio novu IP adresu ili port) — izbaci je,
            // ali tek ako je "offline" duže od 15 s (odmah posle "adb connect" je kratko offline).
            if (isIp && state == "offline")
            {
                if (Strike(serial, TimeSpan.FromSeconds(15))) await Disconnect(adb, serial);
                continue;
            }

            string? hw = usb ? serial : HwSerial(serial);
            if (hw == null) HwCache.TryGetValue(serial, out hw);
            if (hw == null && state == "device")
            {
                var p = (await Proc.RunAsync(adb, $"-s {serial} shell getprop ro.serialno", 5000)).Trim();
                if (p.Length > 0 && !p.Contains(' ')) { hw = p; HwCache[serial] = p; }
                else if (isIp)
                {
                    // adb misli da je povezan, ali telefon ne odgovara — posle drugog neuspeha prekini vezu
                    if (Strike(serial, TimeSpan.FromSeconds(8))) await Disconnect(adb, serial);
                    continue;
                }
            }
            if (state == "device") Suspect.Remove(serial);

            var model = Regex.Match(rest, @"model:(\S+)");
            string? name = model.Success ? model.Groups[1].Value.Replace('_', ' ') : null;
            string key = hw ?? serial;
            if (name != null) s.Names[key] = name;

            result.Add(new DeviceInfo
            {
                Key = key,
                Serial = serial,
                Address = isIp ? serial : null,
                IsUsb = usb,
                State = state,
                Name = name ?? s.Names.GetValueOrDefault(key) ?? key
            });
        }

        foreach (var k in Suspect.Keys.Where(k => !seen.Contains(k)).ToList()) Suspect.Remove(k);

        // Isti telefon može biti povezan dvaput preko mreže (IP:port i adb-…._adb-tls-connect).
        // Zadrži jednu stavku: povezanu, a među povezanima TLS (adb je sam održava).
        result = result
            .GroupBy(d => d.IsUsb ? "usb|" + d.Key : "net|" + d.Key)
            .Select(g => g.OrderByDescending(d => d.State == "device")
                          .ThenByDescending(d => d.Serial?.Contains("._adb-tls-connect") == true)
                          .First())
            .ToList();

        // 2) Telefoni koji se oglašavaju na mreži (Wireless debugging uključen)
        var mdnsOut = await Proc.RunAsync(adb, "mdns services");
        foreach (var raw in mdnsOut.Split('\n'))
        {
            var m = MdnsLine.Match(raw.Trim());
            if (!m.Success) continue;

            string inst = m.Groups[1].Value, addr = m.Groups[2].Value;
            string key = HwSerial(inst) ?? inst;

            var existing = result.FirstOrDefault(d => !d.IsUsb && (d.Key == key || d.Serial == addr));
            if (existing != null)
            {
                if (existing.Serial != null && IpSerial.IsMatch(existing.Serial) && existing.Serial != addr)
                {
                    // Telefon se oglašava na novoj adresi/portu → stara veza je mrtva, poveži se na novu
                    await Disconnect(adb, existing.Serial);
                    existing.Serial = null;
                    existing.State = "available";
                    existing.Address = addr;
                }
                else existing.Address ??= addr;
                continue;
            }

            result.Add(new DeviceInfo
            {
                Key = key,
                Address = addr,
                State = "available",
                Name = s.Names.GetValueOrDefault(key) ?? key
            });
        }

        // Omiljeni je ranije mogao biti sačuvan pod IP:port (kad serijski broj nije bio poznat).
        // Takav ključ prestaje da važi čim telefon dobije novu adresu → prebaci ga na serijski broj.
        if (s.FavoriteKey != null && IpSerial.IsMatch(s.FavoriteKey))
        {
            var d = result.FirstOrDefault(x => (x.Serial == s.FavoriteKey || x.Address == s.FavoriteKey) && x.Key != s.FavoriteKey);
            if (d != null) { s.FavoriteKey = d.Key; s.Save(); }
        }

        foreach (var d in result) d.IsFavorite = d.Key == s.FavoriteKey;
        return result;
    }
}

/// <summary>
/// Traženje telefona bez mDNS-a: telefon prebačen na stalni port 5555 (adb tcpip 5555)
/// pronalazi se proverom svih adresa lokalne mreže na tom portu.
/// </summary>
public static class NetFinder
{
    public const int Port = 5555;
    const int MaxHosts = 4096;   // najviše /20 mreža

    static IEnumerable<IPAddress> LocalHosts()
    {
        var seen = new HashSet<uint>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                uint ip = ToUint(ua.Address), mask = ToUint(ua.IPv4Mask);
                if (mask == 0) continue;
                uint net = ip & mask, bcast = net | ~mask;
                if (bcast - net > MaxHosts) continue; // prevelika mreža
                if ((ip >> 16) == 0xA9FE) continue;  // 169.254.x.x
                for (uint h = net + 1; h < bcast; h++)
                    if (h != ip && seen.Add(h)) yield return FromUint(h);
            }
        }
    }

    static uint ToUint(IPAddress a) { var b = a.GetAddressBytes(); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
    static IPAddress FromUint(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    static async Task<bool> PortOpenAsync(IPAddress ip, int port, int timeoutMs)
    {
        using var c = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await c.ConnectAsync(ip, port, cts.Token); return c.Connected; }
        catch { return false; }
    }

    /// <summary>Sve adrese u lokalnoj mreži na kojima je otvoren port 5555 ("IP:5555").</summary>
    public static async Task<List<string>> ScanAsync(int timeoutMs = 500)
    {
        var found = new List<string>();
        using var gate = new SemaphoreSlim(256);
        var tasks = LocalHosts().Select(async ip =>
        {
            await gate.WaitAsync();
            try
            {
                if (await PortOpenAsync(ip, Port, timeoutMs))
                    lock (found) found.Add($"{ip}:{Port}");
            }
            finally { gate.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
        return found;
    }

    public static Task<bool> IsOpenAsync(string address, int timeoutMs = 800)
    {
        var parts = address.Split(':');
        return parts.Length == 2 && IPAddress.TryParse(parts[0], out var ip) && int.TryParse(parts[1], out var port)
            ? PortOpenAsync(ip, port, timeoutMs)
            : Task.FromResult(false);
    }
}
