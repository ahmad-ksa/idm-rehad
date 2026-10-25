using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rehad;

/// <summary>Base for the dialogs (built in code): body, button row and small layout helpers.</summary>
public class Dlg : Window
{
    protected readonly StackPanel Body = new() { Margin = new Thickness(14, 12, 14, 4) };
    protected readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(10, 6, 10, 12) };

    public Dlg(string title, double width, bool taskbar = false)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = Application.Current?.MainWindow is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        FlowDirection = Loc.Flow;
        ShowInTaskbar = taskbar;
        Background = Brushes.White;
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")); } catch { }
        var dock = new DockPanel();
        var bar = new Border { Background = (Brush)Application.Current.Resources["Panel"], BorderBrush = (Brush)Application.Current.Resources["Line"], BorderThickness = new Thickness(0, 1, 0, 0), Child = Buttons };
        DockPanel.SetDock(bar, Dock.Bottom);
        dock.Children.Add(bar);
        dock.Children.Add(Body);
        Content = dock;
        if (App.Main is { IsVisible: true } m && !taskbar) Owner = m;
    }

    public static TextBlock Label(string text, double top = 6, bool bold = false) =>
        new() { Text = text, Margin = new Thickness(0, top, 0, 3), TextWrapping = TextWrapping.Wrap, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };

    public static TextBox Input(string text = "", double width = double.NaN) =>
        new() { Text = text, Width = width, MinHeight = 24 };

    public Button Btn(string text, Action click, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button { Content = text.Replace("_", ""), IsDefault = isDefault, IsCancel = isCancel, MinWidth = 90, Height = 28 };
        b.Click += (_, _) => click();
        Buttons.Children.Add(b);
        return b;
    }

    public static CheckBox Check(string text, bool value) => new() { Content = text, IsChecked = value };

    /// <summary>Two-column form: label | control.</summary>
    public static Grid Form(params (string label, UIElement control)[] rows)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Length; i++)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = rows[i].label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            Grid.SetRow(l, i);
            g.Children.Add(l);
            var c = rows[i].control;
            if (c is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(c, i); Grid.SetColumn(c, 1);
            g.Children.Add(c);
        }
        return g;
    }

    /// <summary>Control + small button on its side (e.g. path + "...").</summary>
    public static DockPanel WithButton(UIElement main, string text, Action click, double width = 34)
    {
        var d = new DockPanel();
        var b = new Button { Content = text, MinWidth = width, Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (_, _) => click();
        DockPanel.SetDock(b, Dock.Right);
        d.Children.Add(b);
        d.Children.Add(main);
        return d;
    }

    public static StackPanel Row(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        foreach (var i in items)
        {
            if (i is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 0, 8, 0);
            if (i is FrameworkElement f2) f2.VerticalAlignment = VerticalAlignment.Center;
            sp.Children.Add(i);
        }
        return sp;
    }

    public static string? Prompt(Window? owner, string title, string text, string value = "")
    {
        var d = new Dlg(title, 380);
        if (owner != null && owner.IsVisible) d.Owner = owner;
        var box = Input(value);
        d.Body.Children.Add(Label(text, 0));
        d.Body.Children.Add(box);
        string? result = null;
        d.Btn(Loc.T("ok"), () => { result = box.Text.Trim(); d.DialogResult = true; }, isDefault: true);
        d.Btn(Loc.T("cancel"), () => d.DialogResult = false, isCancel: true);
        d.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return d.ShowDialog() == true && result?.Length > 0 ? result : null;
    }
}
