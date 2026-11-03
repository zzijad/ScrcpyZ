using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace ScrcpyZ;

public partial class PatternWindow : Window
{
    const double Cell = 80, Margin0 = 40, HitRadius = 30;

    readonly string _adb;
    readonly Func<Task<string?>> _getSerial;
    readonly Settings _s;
    readonly List<int> _pattern = new();
    bool _drawing, _ready, _setting;
    double _spacingY;          // vertikalni razmak iz automatske detekcije (0 = isto kao horizontalni)
    int _w = 1080, _h = 2400;   // dimenzije ekrana telefona
    Point? _cursor;

    public PatternWindow(string adb, Func<Task<string?>> getSerial, Settings s)
    {
        InitializeComponent();
        _adb = adb;
        _getSerial = getSerial;
        _s = s;

        _pattern.AddRange(s.Pattern.Where(ch => ch is >= '1' and <= '9').Select(ch => ch - '0'));
        ApplyGrid(s.GridCenterX, s.GridCenterY, s.GridSpacing, s.GridSpacingY);
        _ready = true;

        SetPhoneSize(_w, _h);
        RenderPattern();
        RenderOverlay();
    }

    // ---------- 1. Crtanje šablona ----------

    static Point CellCenter(int dot)
    {
        int r = (dot - 1) / 3, c = (dot - 1) % 3;
        return new Point(Margin0 + c * Cell, Margin0 + r * Cell);
    }

    int HitDot(Point p)
    {
        for (int d = 1; d <= 9; d++)
        {
            var c = CellCenter(d);
            if ((c - p).Length <= HitRadius) return d;
        }
        return 0;
    }

    void AddDot(int d)
    {
        if (d == 0 || _pattern.Contains(d)) return;
        if (_pattern.Count > 0)
        {
            // kao na Androidu: preskočena srednja tačka (npr. 1→3) se dodaje automatski
            int a = _pattern[^1];
            int ar = (a - 1) / 3, ac = (a - 1) % 3, br = (d - 1) / 3, bc = (d - 1) % 3;
            if ((ar + br) % 2 == 0 && (ac + bc) % 2 == 0)
            {
                int mid = (ar + br) / 2 * 3 + (ac + bc) / 2 + 1;
                if (mid != a && mid != d && !_pattern.Contains(mid)) _pattern.Add(mid);
            }
        }
        _pattern.Add(d);
    }

    void DrawCanvas_Down(object sender, MouseButtonEventArgs e)
    {
        _pattern.Clear();
        _drawing = true;
        DrawCanvas.CaptureMouse();
        var p = e.GetPosition(DrawCanvas);
        AddDot(HitDot(p));
        _cursor = p;
        RenderPattern();
    }

    void DrawCanvas_Move(object sender, MouseEventArgs e)
    {
        if (!_drawing) return;
        var p = e.GetPosition(DrawCanvas);
        AddDot(HitDot(p));
        _cursor = p;
        RenderPattern();
    }

    void DrawCanvas_Up(object sender, MouseButtonEventArgs e)
    {
        _drawing = false;
        _cursor = null;
        DrawCanvas.ReleaseMouseCapture();
        RenderPattern();
        RenderOverlay();
    }

    void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _pattern.Clear();
        RenderPattern();
        RenderOverlay();
    }

    void RenderPattern()
    {
        DrawCanvas.Children.Clear();
        var accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));

        for (int i = 1; i < _pattern.Count; i++)
            AddLine(DrawCanvas, CellCenter(_pattern[i - 1]), CellCenter(_pattern[i]), accent, 6);
        if (_drawing && _cursor is Point cur && _pattern.Count > 0)
            AddLine(DrawCanvas, CellCenter(_pattern[^1]), cur, accent, 3);

        for (int d = 1; d <= 9; d++)
        {
            bool on = _pattern.Contains(d);
            var c = CellCenter(d);
            double r = on ? 13 : 8;
            var dot = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Fill = on ? accent : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
            };
            Canvas.SetLeft(dot, c.X - r);
            Canvas.SetTop(dot, c.Y - r);
            DrawCanvas.Children.Add(dot);
        }

        PatternText.Text = _pattern.Count == 0
            ? "Šablon nije nacrtan."
            : $"Šablon: {string.Join("-", _pattern)}  ({_pattern.Count} tačaka)";
    }

    static void AddLine(Canvas c, Point a, Point b, Brush brush, double thickness) =>
        c.Children.Add(new Line
        {
            X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
            Stroke = brush, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });

    // ---------- 2. Kalibracija ----------

    void SetPhoneSize(int w, int h)
    {
        _w = w; _h = h;
        Overlay.Width = w; Overlay.Height = h;
        Shot.Width = w; Shot.Height = h;
    }

    void ApplyGrid(double cx, double cy, double sp, double spY)
    {
        _setting = true;
        CenterXSlider.Value = Math.Clamp(cx, CenterXSlider.Minimum, CenterXSlider.Maximum);
        CenterSlider.Value = Math.Clamp(cy, CenterSlider.Minimum, CenterSlider.Maximum);
        SpacingSlider.Value = Math.Clamp(sp, SpacingSlider.Minimum, SpacingSlider.Maximum);
        _spacingY = spY;
        _setting = false;
        if (_ready) RenderOverlay();
    }

    (int x, int y) Dot(int d) => Phone.DotPosition(d, _w, _h, CenterXSlider.Value, CenterSlider.Value,
        SpacingSlider.Value, _spacingY);

    void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready || _setting) return;
        if (sender == SpacingSlider) _spacingY = 0; // ručno podešavanje: isti razmak u oba smera
        RenderOverlay();
    }

    void RenderOverlay()
    {
        CenterXLabel.Text = $"Horizontalni položaj: {CenterXSlider.Value * 100:0.0}% širine";
        CenterLabel.Text = $"Visina srednjeg reda tačaka: {CenterSlider.Value * 100:0.0}% ekrana";
        SpacingLabel.Text = $"Razmak između tačaka: {SpacingSlider.Value * 100:0.0}% širine";

        Overlay.Children.Clear();
        if (Shot.Source == null)
            Overlay.Children.Add(new Rectangle { Width = _w, Height = _h, Fill = new SolidColorBrush(Color.FromRgb(0x33, 0x41, 0x55)) });

        var accent = new SolidColorBrush(Color.FromArgb(0xDD, 0x3B, 0x82, 0xF6));
        double lw = _w * 0.012;

        for (int i = 1; i < _pattern.Count; i++)
        {
            var (ax, ay) = Dot(_pattern[i - 1]);
            var (bx, by) = Dot(_pattern[i]);
            AddLine(Overlay, new Point(ax, ay), new Point(bx, by), accent, lw);
        }

        double r = _w * 0.045;
        for (int d = 1; d <= 9; d++)
        {
            var (x, y) = Dot(d);
            var ring = new Ellipse
            {
                Width = r * 2, Height = r * 2,
                Stroke = accent, StrokeThickness = _w * 0.008,
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x3B, 0x82, 0xF6))
            };
            Canvas.SetLeft(ring, x - r);
            Canvas.SetTop(ring, y - r);
            Overlay.Children.Add(ring);
        }
    }

    async void BtnShot_Click(object sender, RoutedEventArgs e)
    {
        BtnShot.IsEnabled = false;
        try
        {
            StatusText.Text = "Povezujem telefon…";
            var serial = await _getSerial();
            if (serial == null) { StatusText.Text = "Telefon nije povezan. Izaberi ga u glavnom prozoru."; return; }

            StatusText.Text = "Budim telefon i tražim tačke…";
            bool locked = await Phone.WakeAndSwipeAsync(_adb, serial);
            if (!locked)
            {
                StatusText.Text = "Telefon je otključan — zaključaj ga (dugme za napajanje) pa klikni ponovo.";
                return;
            }
            var (w, h) = await Phone.ScreenSizeAsync(_adb, serial);
            SetPhoneSize(w, h);

            var grid = await Phone.FindPatternGridAsync(_adb, serial, w, h);
            if (grid != null) ApplyGrid(grid.CenterX, grid.CenterY, grid.Spacing, grid.SpacingY);

            var png = await Proc.RunBytesAsync(_adb, $"-s {serial} exec-out screencap -p");
            Shot.Source = LoadPng(png);
            RenderOverlay();

            StatusText.Text = grid != null
                ? "Tačke su pronađene automatski. Klikni „Probaj na telefonu“."
                : "Tačke nisu pronađene automatski" +
                  (Shot.Source == null ? " i snimak ekrana nije uspeo" : "") +
                  ". Podesi klizačima i proveri dugmetom „Probaj na telefonu“.";
        }
        finally { BtnShot.IsEnabled = true; }
    }

    static BitmapImage? LoadPng(byte[] data)
    {
        if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50) return null; // nije PNG
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = new MemoryStream(data);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    async void BtnTest_Click(object sender, RoutedEventArgs e)
    {
        if (_pattern.Count < 2) { StatusText.Text = "Prvo nacrtaj šablon (bar 2 tačke)."; return; }
        BtnTest.IsEnabled = false;
        string? serial = null;
        try
        {
            serial = await _getSerial();
            if (serial == null) { StatusText.Text = "Telefon nije povezan."; return; }

            StatusText.Text = "Crtam šablon na telefonu…";
            if (!await Phone.WakeAndSwipeAsync(_adb, serial))
            {
                StatusText.Text = "Telefon je već otključan — zaključaj ga pa probaj ponovo.";
                return;
            }
            var (w, h) = await Phone.ScreenSizeAsync(_adb, serial);
            SetPhoneSize(w, h);

            // prikaži trag dodira na telefonu, da se vidi kuda je potez prošao
            await Phone.SetPointerLocationAsync(_adb, serial, true);
            await Phone.DrawPatternAsync(_adb, serial, string.Concat(_pattern), w, h,
                CenterXSlider.Value, CenterSlider.Value, SpacingSlider.Value, _spacingY);
            await Task.Delay(900);

            StatusText.Text = await Phone.IsLockedAsync(_adb, serial) switch
            {
                false => "Radi! Telefon je otključan. Klikni „Sačuvaj“.",
                true => "Telefon je i dalje zaključan. Pogledaj trag na telefonu, pomeri tačke klizačima pa probaj ponovo " +
                        "(posle 5 pogrešnih pokušaja telefon traži pauzu od 30 s).",
                _ => "Šablon je poslat — pogledaj telefon."
            };
            await Task.Delay(2500);
        }
        finally
        {
            if (serial != null) await Phone.SetPointerLocationAsync(_adb, serial, false);
            BtnTest.IsEnabled = true;
        }
    }

    void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (_pattern.Count < 4)
        {
            StatusText.Text = "Android šablon ima najmanje 4 tačke.";
            return;
        }
        _s.Pattern = string.Concat(_pattern);
        _s.GridCenterX = CenterXSlider.Value;
        _s.GridCenterY = CenterSlider.Value;
        _s.GridSpacing = SpacingSlider.Value;
        _s.GridSpacingY = _spacingY;
        _s.LockType = "pattern";
        _s.Save();
        DialogResult = true;
    }
}
