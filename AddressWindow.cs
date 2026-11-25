using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace ScrcpyZ;

/// <summary>Mali prozor za unos IP:port (bez XAML-a).</summary>
public class AddressWindow : Window
{
    readonly TextBox _txt;
    readonly TextBlock _err;
    public string Address { get; private set; } = "";

    public AddressWindow(string lastIp)
    {
        Title = "Poveži na adresu";
        Width = 380; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"); FontSize = 13;

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = "Na telefonu: Developer options → Wireless debugging.\nUpiši IP adresu i port koji tamo piše (npr. 10.19.53.37:41235).",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8)
        });
        _txt = new TextBox { Text = lastIp.Length > 0 ? lastIp + ":" : "", Padding = new Thickness(4, 3, 4, 3) };
        sp.Children.Add(_txt);
        _err = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) };
        sp.Children.Add(_err);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "Poveži", IsDefault = true, Padding = new Thickness(16, 5, 16, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Otkaži", IsCancel = true, Padding = new Thickness(16, 5, 16, 5) };
        ok.Click += (_, _) =>
        {
            var a = Regex.Replace(_txt.Text.Trim(), @"\s+", "");
            if (!Regex.IsMatch(a, @"^\d{1,3}(\.\d{1,3}){3}:\d{1,5}$")) { _err.Text = "Unesi adresu u obliku IP:port."; return; }
            Address = a;
            DialogResult = true;
        };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        sp.Children.Add(buttons);
        Content = sp;

        Loaded += (_, _) => { _txt.Focus(); _txt.CaretIndex = _txt.Text.Length; };
    }
}
