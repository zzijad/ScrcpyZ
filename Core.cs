using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
    public static string? HwSerial(string name)
    {
        int i = name.IndexOf("._adb-tls-connect", StringComparison.Ordinal);
        if (i >= 0) name = name[..i];
        var m = Instance.Match(name);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static async Task<List<DeviceInfo>> ScanAsync(string adb, Settings s)
    {
        var result = new List<DeviceInfo>();

        // 1) Već povezani uređaji (USB i bežični)
        var devOut = await Proc.RunAsync(adb, "devices -l");
        foreach (var raw in devOut.Split('\n').Skip(1))
        {
            var m = DevLine.Match(raw.Trim());
            if (!m.Success) continue;

            string serial = m.Groups[1].Value, state = m.Groups[2].Value, rest = m.Groups[3].Value;
            bool isTls = serial.Contains("._adb-tls-connect");
            bool isIp = IpSerial.IsMatch(serial);
            bool usb = !isTls && !isIp;

            string? hw = usb ? serial : HwSerial(serial);
            if (hw == null && state == "device")
            {
                if (!HwCache.TryGetValue(serial, out hw))
                {
                    var p = (await Proc.RunAsync(adb, $"-s {serial} shell getprop ro.serialno", 5000)).Trim();
                    if (p.Length > 0 && !p.Contains(' ')) { hw = p; HwCache[serial] = p; }
                }
            }

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

        // 2) Telefoni koji se oglašavaju na mreži (Wireless debugging uključen)
        var mdnsOut = await Proc.RunAsync(adb, "mdns services");
        foreach (var raw in mdnsOut.Split('\n'))
        {
            var m = MdnsLine.Match(raw.Trim());
            if (!m.Success) continue;

            string inst = m.Groups[1].Value, addr = m.Groups[2].Value;
            string key = HwSerial(inst) ?? inst;

            var existing = result.FirstOrDefault(d => !d.IsUsb && (d.Key == key || d.Serial == addr));
            if (existing != null) { existing.Address ??= addr; continue; }

            result.Add(new DeviceInfo
            {
                Key = key,
                Address = addr,
                State = "available",
                Name = s.Names.GetValueOrDefault(key) ?? key
            });
        }

        foreach (var d in result) d.IsFavorite = d.Key == s.FavoriteKey;
        return result;
    }
}
