using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ScrcpyZ;

public partial class MainWindow : Window
{
    readonly Settings _s = Settings.Load();
    readonly ObservableCollection<DeviceInfo> _devices = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    string? _scrcpyDir;
    bool _scanning, _autoLaunched, _serverStarted, _launching;
    string? _currentKey;
    Process? _scrcpy;
    string? _currentSerial;
    TrayIcon? _tray;
    DateTime _nextAutoTry = DateTime.MinValue;

    string Adb => _scrcpyDir != null && File.Exists(Path.Combine(_scrcpyDir, "adb.exe"))
        ? Path.Combine(_scrcpyDir, "adb.exe")
        : "adb";

    public MainWindow()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        LoadOptions();

        _timer.Tick += async (_, _) =>
        {
            if (ChkAutoRefresh.IsChecked == true) await RefreshAsync();
        };
        Loaded += async (_, _) =>
        {
            LocateScrcpy();
            await RefreshAsync();
            _timer.Start();
        };
        Closing += (_, _) => { SaveOptions(); _tray?.Dispose(); _tray = null; };
        StateChanged += (_, _) =>
        {
            // minimizovanje dok scrcpy radi → u tray
            if (WindowState == WindowState.Minimized && ScrcpyRunning && ChkTray.IsChecked == true)
                HideToTray();
        };
    }

    // ---------- Podešavanja ----------

    void LoadOptions()
    {
        ChkScreenOff.IsChecked = _s.ScreenOff;
        ChkStayAwake.IsChecked = _s.StayAwake;
        ChkNoAudio.IsChecked = _s.NoAudio;
        ChkOnTop.IsChecked = _s.OnTop;
        ChkFullscreen.IsChecked = _s.Fullscreen;
        TxtMaxSize.Text = _s.MaxSize.ToString();
        TxtBitrate.Text = _s.Bitrate.ToString();
        TxtExtra.Text = _s.Extra;
        ChkAutoConnect.IsChecked = _s.AutoConnect;
        ChkAutoRefresh.IsChecked = _s.AutoRefresh;
        ChkAutoUnlock.IsChecked = _s.AutoUnlock;
        ChkTray.IsChecked = _s.TrayAfterConnect;
        PinBox.Password = _s.Pin;
        if (_s.LockType == "pattern") RbPattern.IsChecked = true; else RbPin.IsChecked = true;
        UpdateLockUi();
    }

    void UpdateLockUi()
    {
        bool pattern = RbPattern.IsChecked == true;
        PatternPanel.IsEnabled = pattern;
        PinBox.IsEnabled = !pattern;
        int n = _s.Pattern.Length;
        PatternStatus.Text = n >= 4 ? $"Šablon je podešen ({n} tačaka)." : "Šablon nije podešen.";
    }

    void LockType_Changed(object sender, RoutedEventArgs e)
    {
        if (PatternPanel == null || PinBox == null) return; // tokom InitializeComponent
        UpdateLockUi();
    }

    void BtnPattern_Click(object sender, RoutedEventArgs e)
    {
        SaveOptions();
        var w = new PatternWindow(Adb, SelectedSerialAsync, _s) { Owner = this };
        if (w.ShowDialog() == true)
        {
            RbPattern.IsChecked = true;
            Status("Šablon je sačuvan.");
        }
        UpdateLockUi();
    }

    /// <summary>Serial izabranog telefona (povezuje ga ako treba), ili telefona u scrcpy-ju.</summary>
    async Task<string?> SelectedSerialAsync()
    {
        if (DeviceList.SelectedItem is DeviceInfo d) return await EnsureConnectedAsync(d);
        return ScrcpyRunning ? _currentSerial : null;
    }

    void SaveOptions()
    {
        _s.ScreenOff = ChkScreenOff.IsChecked == true;
        _s.StayAwake = ChkStayAwake.IsChecked == true;
        _s.NoAudio = ChkNoAudio.IsChecked == true;
        _s.OnTop = ChkOnTop.IsChecked == true;
        _s.Fullscreen = ChkFullscreen.IsChecked == true;
        _s.MaxSize = int.TryParse(TxtMaxSize.Text, out var ms) ? Math.Max(0, ms) : 0;
        _s.Bitrate = int.TryParse(TxtBitrate.Text, out var br) ? Math.Max(0, br) : 8;
        _s.Extra = TxtExtra.Text.Trim();
        _s.AutoConnect = ChkAutoConnect.IsChecked == true;
        _s.AutoRefresh = ChkAutoRefresh.IsChecked == true;
        _s.AutoUnlock = ChkAutoUnlock.IsChecked == true;
        _s.TrayAfterConnect = ChkTray.IsChecked == true;
        _s.Pin = PinBox.Password;
        _s.LockType = RbPattern.IsChecked == true ? "pattern" : "pin";
        _s.Save();
    }

    void LocateScrcpy()
    {
        _scrcpyDir = Proc.FindScrcpyDir(_s.ScrcpyFolder);
        ScrcpyPathText.Text = _scrcpyDir != null
            ? "scrcpy: " + _scrcpyDir
            : "scrcpy nije pronađen — izaberi folder (dugme „Folder scrcpy-ja…“) ili kopiraj ScrcpyZ u scrcpy folder.";
        _serverStarted = false;
    }

    void Status(string text) => StatusText.Text = text;

    // ---------- Pretraga telefona ----------

    async Task RefreshAsync()
    {
        if (_scanning || _launching) return;
        if (_scrcpyDir == null && !AdbOnPath())
        {
            Status("adb nije pronađen. Izaberi folder u kom su scrcpy.exe i adb.exe.");
            return;
        }

        _scanning = true;
        try
        {
            if (!_serverStarted)
            {
                Status("Pokrećem adb…");
                await Proc.StartAdbServerAsync(Adb);
                _serverStarted = true;
            }

            var found = await DeviceScanner.ScanAsync(Adb, _s);
            UpdateList(found);

            Status(found.Count == 0
                ? "Nema telefona. Na telefonu uključi Wireless debugging (ista Wi-Fi mreža) ili ga poveži USB kablom."
                : $"Pronađeno telefona: {found.Count}. Dupli klik ili „Poveži“ za pokretanje.");

            if (!ScrcpyRunning && !_launching && _s.SavedTimeoutKey != null)
                await RestoreTimeoutAsync();

            if (ChkAutoConnect.IsChecked == true && !_autoLaunched && !_launching && _s.FavoriteKey != null
                && !ScrcpyRunning && DateTime.UtcNow >= _nextAutoTry)
            {
                var fav = _devices.FirstOrDefault(d => d.IsFavorite && d.State is "device" or "available");
                if (fav != null)
                {
                    _autoLaunched = true;
                    _scanning = false; // ConnectAndLaunch sam osvežava listu na kraju
                    await ConnectAndLaunchAsync(fav, auto: true);
                }
            }
        }
        catch (Exception ex)
        {
            Status("Greška: " + ex.Message);
        }
        finally
        {
            _scanning = false;
        }
    }

    static bool AdbOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(d => { try { return File.Exists(Path.Combine(d, "adb.exe")); } catch { return false; } });

    void UpdateList(List<DeviceInfo> found)
    {
        // Ne diraj listu ako se ništa nije promenilo (da selekcija ne skače)
        if (found.Select(d => d.Signature).SequenceEqual(_devices.Select(d => d.Signature))) return;

        var selKey = (DeviceList.SelectedItem as DeviceInfo)?.Key;
        _devices.Clear();
        foreach (var d in found) _devices.Add(d);

        DeviceList.SelectedItem =
            _devices.FirstOrDefault(d => d.Key == selKey)
            ?? _devices.FirstOrDefault(d => d.IsFavorite)
            ?? _devices.FirstOrDefault();
    }

    // ---------- Povezivanje i pokretanje ----------

    bool ScrcpyRunning
    {
        get { try { return _scrcpy != null && !_scrcpy.HasExited; } catch { return false; } }
    }

    /// <summary>Povezuje telefon ako već nije povezan i vraća adb serial (ili null).</summary>
    async Task<string?> EnsureConnectedAsync(DeviceInfo d)
    {
        if (d.State == "unauthorized")
        {
            Status("Na telefonu potvrdi „Allow USB debugging“, pa pokušaj ponovo.");
            return null;
        }
        if (d.Serial != null && d.State == "device") return d.Serial;
        if (d.Address != null)
        {
            Status($"Povezujem {d.Name} ({d.Address})…");
            var r = await Proc.RunAsync(Adb, $"connect {d.Address}", 15000);
            if (r.Contains("connected to")) return d.Address;
            Status("Povezivanje nije uspelo: " + r.Trim() +
                   " — ako telefon nije uparen, klikni „Upari telefon…“.");
            return null;
        }
        Status($"Telefon nije spreman ({d.StatusText}).");
        return null;
    }

    async Task ConnectAndLaunchAsync(DeviceInfo d, bool auto = false)
    {
        if (_launching) return;
        if (_scrcpyDir == null)
        {
            MessageBox.Show(this, "scrcpy.exe nije pronađen. Izaberi folder scrcpy-ja.", "ScrcpyZ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ScrcpyRunning)
        {
            if (_currentKey == d.Key)
            {
                _scrcpy!.Refresh();
                await ScrcpyKeys.BringToFrontAsync(_scrcpy.MainWindowHandle);
                Status("scrcpy je već pokrenut za ovaj telefon.");
                return;
            }
            if (MessageBox.Show(this, "scrcpy već radi za drugi telefon. Zatvoriti ga i povezati ovaj?", "ScrcpyZ",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { _scrcpy!.Kill(true); _scrcpy.WaitForExit(3000); } catch { }
        }

        _launching = true;
        _autoLaunched = true; // ručno pokretanje: automatsko više ne treba
        bool started = false;
        SaveOptions();
        BtnConnect.IsEnabled = false;
        try
        {
            var serial = await EnsureConnectedAsync(d);
            if (serial == null) return;

            if (_s.AutoUnlock)
            {
                Status("Otključavam telefon…");
                Status(await Phone.UnlockAsync(Adb, serial, _s));
            }

            if (_s.StayAwake) await KeepAwakeAsync(serial, d.Key);

            started = Launch(serial, d.Name, d.Key);
        }
        finally
        {
            _launching = false;
            BtnConnect.IsEnabled = true;
            if (auto && !started)
            {
                // automatsko povezivanje nije uspelo — probaj ponovo za 20 s (ranije je odustajao zauvek)
                _autoLaunched = false;
                _nextAutoTry = DateTime.UtcNow.AddSeconds(20);
            }
        }
        await RefreshAsync();
    }

    /// <summary>
    /// --stay-awake radi samo kad je telefon na punjaču/USB-u. Preko Wi-Fi-ja privremeno
    /// produžimo vreme gašenja ekrana, a staru vrednost vraćamo kad se scrcpy zatvori.
    /// </summary>
    async Task KeepAwakeAsync(string serial, string key)
    {
        var current = await Phone.GetScreenTimeoutAsync(Adb, serial);
        if (current == null) return;
        if (current != Phone.KeepAwakeTimeout)
        {
            _s.SavedTimeoutKey = key;
            _s.SavedTimeoutValue = current;
            _s.Save();
        }
        await Phone.SetScreenTimeoutAsync(Adb, serial, Phone.KeepAwakeTimeout);
    }

    async Task RestoreTimeoutAsync(string? serial = null)
    {
        if (_s.SavedTimeoutKey == null || _s.SavedTimeoutValue == null) return;
        serial ??= _devices.FirstOrDefault(d => d.Key == _s.SavedTimeoutKey && d.State == "device")?.Serial;
        if (serial == null) return; // telefon trenutno nije povezan — vratiće se kasnije
        await Phone.SetScreenTimeoutAsync(Adb, serial, _s.SavedTimeoutValue);
        // obriši sačuvanu vrednost tek kad se potvrdi da je vraćena (posle prekida veze komanda ne prolazi)
        if (await Phone.GetScreenTimeoutAsync(Adb, serial) != _s.SavedTimeoutValue) return;
        _s.SavedTimeoutKey = null;
        _s.SavedTimeoutValue = null;
        _s.Save();
    }

    bool Launch(string serial, string name, string key)
    {
        var psi = new ProcessStartInfo(Path.Combine(_scrcpyDir!, "scrcpy.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _scrcpyDir!,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["ADB"] = Adb;
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add(serial);
        psi.ArgumentList.Add("--window-title=" + name);
        foreach (var a in BuildArgs()) psi.ArgumentList.Add(a);

        var log = new StringBuilder();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        DataReceivedEventHandler collect = (_, e) =>
        {
            if (e.Data != null) lock (log) log.AppendLine(e.Data);
        };
        p.OutputDataReceived += collect;
        p.ErrorDataReceived += collect;
        p.Exited += (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            if (_scrcpy == p && !IsVisible) ShowFromTray(); // scrcpy zatvoren → vrati ScrcpyZ
            if (_s.SavedTimeoutKey == key) await RestoreTimeoutAsync(serial);
            int code;
            try { code = p.ExitCode; } catch { code = 0; }
            if (code == 1)
            {
                string text;
                lock (log) text = string.Join(Environment.NewLine,
                    log.ToString().Split(Environment.NewLine).TakeLast(15));
                MessageBox.Show(this, "scrcpy se zatvorio sa greškom:\n\n" + text, "ScrcpyZ",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Status("scrcpy se zatvorio sa greškom.");
            }
            else if (code == 2)
            {
                Status($"Veza sa telefonom „{name}“ je prekinuta.");
                // veza pukla (nova IP, telefon van dometa) — kad se telefon ponovo pojavi, poveži ga sam
                if (_scrcpy == p) { _autoLaunched = false; _nextAutoTry = DateTime.UtcNow.AddSeconds(5); }
            }
            else
                Status("scrcpy je zatvoren.");
        });

        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _scrcpy = p;
            _currentSerial = serial;
            _currentKey = key;
            Status($"scrcpy pokrenut za „{name}“ ({serial}).");
            if (_s.ScreenOff) _ = ScreenOffAfterStartAsync(p);
            if (_s.TrayAfterConnect) _ = TrayAfterStartAsync(p, name);
            return true;
        }
        catch (Exception ex)
        {
            Status("Ne mogu da pokrenem scrcpy: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// --turn-screen-off ne radi ako se ekran kasnije probudi, pa po pokretanju
    /// sačekamo prozor scrcpy-ja i pošaljemo mu Alt+O.
    /// </summary>
    async Task ScreenOffAfterStartAsync(Process p)
    {
        var hwnd = await ScrcpyKeys.WaitForWindowAsync(p);
        if (hwnd == IntPtr.Zero) return;
        await Task.Delay(1500); // da se prikaz stabilizuje
        for (int i = 0; i < 3 && !p.HasExited; i++)
        {
            if (await ScrcpyKeys.SendAltOAsync(hwnd)) return;
            await Task.Delay(1000);
        }
    }

    /// <summary>Kad se pojavi prozor scrcpy-ja, sakrij ScrcpyZ u tray.</summary>
    async Task TrayAfterStartAsync(Process p, string name)
    {
        var hwnd = await ScrcpyKeys.WaitForWindowAsync(p);
        if (hwnd == IntPtr.Zero || _scrcpy != p || !ScrcpyRunning) return;
        HideToTray(name);
    }

    // ---------- Tray ----------

    void HideToTray(string? phone = null)
    {
        _tray ??= CreateTray();
        phone ??= _devices.FirstOrDefault(d => d.Key == _currentKey)?.Name;
        _tray.Show(phone != null ? $"ScrcpyZ — {phone}" : "ScrcpyZ");
        Hide();
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _tray?.Remove();
    }

    TrayIcon CreateTray()
    {
        var t = new TrayIcon();
        t.Activate += ShowFromTray;

        t.Items.Add(new TrayMenuItem("Otvori ScrcpyZ", ShowFromTray, IsDefault: true));
        t.Items.Add(new TrayMenuItem("Prikaži scrcpy prozor", async () =>
        {
            if (!ScrcpyRunning || _scrcpy == null) return;
            _scrcpy.Refresh();
            await ScrcpyKeys.BringToFrontAsync(_scrcpy.MainWindowHandle);
        }));
        t.Items.Add(TrayMenuItem.Separator);
        t.Items.Add(new TrayMenuItem("Otključaj telefon", () => BtnUnlock_Click(this, new RoutedEventArgs())));
        t.Items.Add(new TrayMenuItem("Ugasi ekran telefona", () => BtnScreenOff_Click(this, new RoutedEventArgs())));
        t.Items.Add(TrayMenuItem.Separator);
        t.Items.Add(new TrayMenuItem("Zatvori ScrcpyZ", Close));
        return t;
    }

    List<string> BuildArgs()
    {
        var a = new List<string>();
        if (_s.ScreenOff) a.Add("--turn-screen-off");
        if (_s.StayAwake) a.Add("--stay-awake");
        if (_s.NoAudio) a.Add("--no-audio");
        if (_s.OnTop) a.Add("--always-on-top");
        if (_s.Fullscreen) a.Add("--fullscreen");
        if (_s.MaxSize > 0) a.Add($"--max-size={_s.MaxSize}");
        if (_s.Bitrate > 0) a.Add($"--video-bit-rate={_s.Bitrate}M");
        a.AddRange(Regex.Matches(_s.Extra, "\"([^\"]*)\"|(\\S+)")
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value));
        return a;
    }

    // ---------- Dugmad ----------

    async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceInfo d) await ConnectAndLaunchAsync(d);
        else Status("Izaberi telefon iz liste.");
    }

    async void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DeviceList.SelectedItem is DeviceInfo d) await ConnectAndLaunchAsync(d);
    }

    async void BtnUnlock_Click(object sender, RoutedEventArgs e)
    {
        SaveOptions();
        var serial = await SelectedSerialAsync();
        if (serial == null) { if (DeviceList.SelectedItem == null) Status("Izaberi telefon iz liste."); return; }

        Status("Otključavam telefon…");
        Status(await Phone.UnlockAsync(Adb, serial, _s));
    }

    async void BtnScreenOff_Click(object sender, RoutedEventArgs e)
    {
        if (!ScrcpyRunning || _scrcpy == null)
        {
            Status("scrcpy nije pokrenut — prvo klikni „Poveži“.");
            return;
        }
        _scrcpy.Refresh();
        if (await ScrcpyKeys.SendAltOAsync(_scrcpy.MainWindowHandle))
            Status("Ekran telefona je ugašen (prikaz u scrcpy-ju ostaje). Alt+Shift+O ga pali.");
        else
            Status("Prozor scrcpy-ja nije mogao da dobije fokus — klikni na njega i pritisni Alt+O.");
    }

    async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    async void BtnFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not DeviceInfo d) { Status("Izaberi telefon iz liste."); return; }
        if (_s.FavoriteKey != d.Key && DeviceScanner.IsIpKey(d.Key))
        {
            // ključ bi bio IP adresa, koja se menja → omiljeni ne bi radio posle nove adrese
            Status("Serijski broj telefona još nije poznat. Klikni „Poveži“, pa ponovo „★ Omiljeni“.");
            return;
        }
        _s.FavoriteKey = _s.FavoriteKey == d.Key ? null : d.Key;
        _s.Save();
        Status(_s.FavoriteKey == null ? "Omiljeni telefon uklonjen." : $"„{d.Name}“ je omiljeni telefon.");
        _devices.Clear(); // prisilno osvežavanje zvezdice
        await RefreshAsync();
    }

    async void BtnPair_Click(object sender, RoutedEventArgs e)
    {
        var w = new PairWindow(Adb) { Owner = this };
        if (w.ShowDialog() == true) await RefreshAsync();
    }

    async void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Izaberi folder u kom je scrcpy.exe" };
        if (_scrcpyDir != null) dlg.InitialDirectory = _scrcpyDir;
        if (dlg.ShowDialog(this) != true) return;

        if (!File.Exists(Path.Combine(dlg.FolderName, "scrcpy.exe")))
        {
            MessageBox.Show(this, "U tom folderu nema scrcpy.exe.", "ScrcpyZ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _s.ScrcpyFolder = dlg.FolderName;
        _s.Save();
        LocateScrcpy();
        await RefreshAsync();
    }
}
