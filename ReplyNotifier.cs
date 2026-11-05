using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ProfileRefresher;

/// <summary>
/// A small always-on-top window in the bottom-right corner that lists people who replied after
/// the whole message sequence was sent. Each entry stays until you dismiss it, shows the Chrome
/// profile it belongs to, and can bring that profile's window to the front.
/// </summary>
public sealed class ReplyNotifier
{
    private Window? _window;
    private StackPanel? _list;
    private bool _shuttingDown;

    public ReplyNotifier() { }

    public void Add(string profileName, string from, ReplyAlert alert, Action open)
    {
        EnsureWindow();

        var entry = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 6, 0, 8),
        };
        var body = new StackPanel();
        entry.Child = body;

        body.Children.Add(new TextBlock
        {
            Text = $"Chrome profile: {profileName}",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{from} replied at {alert.At:HH:mm}",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(alert.Message))
        {
            body.Children.Add(new TextBlock
            {
                Text = $"“{Shorten(alert.Message!, 160)}”",
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var openBtn = new Button
        {
            Content = "Open profile",
            Padding = new Thickness(10, 2, 10, 2),
            ToolTip = "Bring this Chrome profile's window to the front",
        };
        var dismissBtn = new Button { Content = "Dismiss", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
        openBtn.Click += (_, _) => open();
        dismissBtn.Click += (_, _) => Remove(entry);
        buttons.Children.Add(openBtn);
        buttons.Children.Add(dismissBtn);
        body.Children.Add(buttons);

        _list!.Children.Insert(0, entry); // newest on top
        UpdateTitle();
        if (!_window!.IsVisible) _window.Show();
        PlaceBottomRight();

        try { SystemSounds.Exclamation.Play(); } catch { }
    }

    private void Remove(UIElement entry)
    {
        _list?.Children.Remove(entry);
        if (_list is null || _list.Children.Count == 0) _window?.Hide();
        else UpdateTitle();
    }

    private void EnsureWindow()
    {
        if (_window is not null) return;

        _list = new StackPanel();
        var clearAll = new Button
        {
            Content = "Dismiss all",
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(10, 2, 10, 2),
            Margin = new Thickness(0, 8, 0, 0),
        };
        clearAll.Click += (_, _) =>
        {
            _list.Children.Clear();
            _window?.Hide();
        };

        var root = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(clearAll, Dock.Bottom);
        root.Children.Add(clearAll);
        root.Children.Add(new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420,
        });

        var win = new Window
        {
            Title = "New replies",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
            Topmost = true,
            ShowActivated = false,   // never steal focus from the browser the app is driving
            ShowInTaskbar = false,
            Content = root,
        };
        // Not owned by the main window, so it stays visible when the app is minimized.
        win.SizeChanged += (_, _) => PlaceBottomRight();
        // Closing with ✕ just hides it; entries are kept until dismissed.
        win.Closing += (_, e) =>
        {
            if (_shuttingDown) return;
            e.Cancel = true;
            win.Hide();
        };
        _window = win;
    }

    private void UpdateTitle()
    {
        int n = _list?.Children.Count ?? 0;
        if (_window is not null)
            _window.Title = n == 1 ? "1 new reply after your last message" : $"{n} new replies after your last message";
    }

    private void PlaceBottomRight()
    {
        if (_window is null) return;
        var area = SystemParameters.WorkArea;
        _window.Left = area.Right - _window.ActualWidth - 12;
        _window.Top = area.Bottom - _window.ActualHeight - 12;
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Closes the window for good (when the app exits).</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        try { _window?.Close(); } catch { }
        _window = null;
    }
}
