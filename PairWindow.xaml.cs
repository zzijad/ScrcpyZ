using System.Text.RegularExpressions;
using System.Windows;

namespace ScrcpyZ;

public partial class PairWindow : Window
{
    readonly string _adb;

    public PairWindow(string adb)
    {
        InitializeComponent();
        _adb = adb;
        Loaded += (_, _) => TxtAddress.Focus();
    }

    async void BtnPair_Click(object sender, RoutedEventArgs e)
    {
        var addr = TxtAddress.Text.Trim();
        var code = TxtCode.Text.Trim();
        if (!Regex.IsMatch(addr, @"^[\w\.\-]+:\d+$")) { ResultText.Text = "Unesi adresu u obliku IP:port."; return; }
        if (!Regex.IsMatch(code, @"^\d{6}$")) { ResultText.Text = "Kod ima 6 cifara."; return; }

        BtnPair.IsEnabled = false;
        ResultText.Text = "Uparujem…";
        var r = await Proc.RunAsync(_adb, $"pair {addr} {code}", 20000);
        BtnPair.IsEnabled = true;

        if (r.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Telefon je uparen. Od sada se pronalazi automatski.", "ScrcpyZ");
            DialogResult = true;
        }
        else
        {
            ResultText.Text = "Uparivanje nije uspelo: " + r.Trim();
        }
    }
}
