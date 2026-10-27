using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SoundDeck;

// The flyout by the tray: output devices on top, programs below.
internal sealed class PanelWindow : Window
{
    const double PanelWidth = 392;

    readonly AudioService _audio;
    readonly Border _card;
    readonly StackPanel _devices = new();
    readonly StackPanel _apps = new();
    readonly TextBlock _noApps;
    readonly TextBlock _footer;
    readonly FlatButton _switchButton;
    readonly TextBlock _switchText;
    readonly Dictionary<string, DeviceRow> _deviceRows = new();
    readonly Dictionary<string, AppRow> _appRows = new();
    readonly DispatcherTimer _meters;
    readonly TranslateTransform _slide = new();

    public DateTime HiddenAt { get; private set; }

    public PanelWindow(AudioService audio)
    {
        _audio = audio;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = PanelWidth + 24;
        SizeToContent = SizeToContent.Height;
        Title = "Звуковой пульт";

        var title = Theme.Label("Звуковой пульт", 15, weight: FontWeights.SemiBold);
        _switchText = Theme.Label("", 12, Theme.Dim);
        var switchContent = new StackPanel { Orientation = Orientation.Horizontal };
        switchContent.Children.Add(Theme.Glyph("", 12, Theme.AccentBrush));
        _switchText.Margin = new Thickness(6, 0, 0, 0);
        switchContent.Children.Add(_switchText);
        _switchButton = new FlatButton(switchContent, "Переключить вывод по умолчанию");
        _switchButton.Click += () =>
        {
            _audio.SwitchToNext();
            Update();
        };

        var header = new DockPanel { Margin = new Thickness(4, 0, 0, 8), LastChildFill = false };
        DockPanel.SetDock(_switchButton, Dock.Right);
        header.Children.Add(_switchButton);
        header.Children.Add(title);

        _noApps = Theme.Label("Сейчас никакая программа не работает со звуком", 12, Theme.Faint);
        _noApps.Margin = new Thickness(6, 4, 6, 8);

        var appsScroll = new ScrollViewer
        {
            Content = _apps,
            MaxHeight = 430,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };

        _footer = Theme.Label("", 11, Theme.Faint);
        _footer.TextWrapping = TextWrapping.Wrap;
        _footer.TextTrimming = TextTrimming.None;
        _footer.Margin = new Thickness(6, 10, 6, 0);

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(Section("ВЫВОД"));
        content.Children.Add(_devices);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = Theme.Brush(0x20, 0xFF, 0xFF, 0xFF),
            Margin = new Thickness(4, 10, 4, 6),
        });
        content.Children.Add(Section("ПРОГРАММЫ"));
        content.Children.Add(_noApps);
        content.Children.Add(appsScroll);
        content.Children.Add(_footer);

        _card = new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 12, 12, 12),
            Margin = new Thickness(12),
            Child = content,
            RenderTransform = _slide,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 3,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };
        Content = _card;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _meters = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _meters.Tick += (_, _) => UpdateMeters();

        Deactivated += (_, _) => HidePanel();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                HidePanel();
            }
        };
        SizeChanged += (_, _) => PlaceNearTray();
        // Not in Alt+Tab.
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long style = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64() | Native.WS_EX_TOOLWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
        };
    }

    static TextBlock Section(string text)
    {
        var label = Theme.Label(text, 10.5, Theme.Faint, FontWeights.SemiBold);
        label.Margin = new Thickness(6, 2, 0, 4);
        return label;
    }

    // ------------------------------------------------------------ show / hide

    public void Toggle()
    {
        if (IsVisible)
        {
            HidePanel();
        }
        // A click on the tray icon first deactivates the panel, then arrives
        // here: that click meant "close".
        else if ((DateTime.UtcNow - HiddenAt).TotalMilliseconds > 300)
        {
            ShowPanel();
        }
    }

    public void ShowPanel()
    {
        _audio.Refresh();
        Update();
        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }
        // The panel needs to be active to notice a click elsewhere.
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        PlaceNearTray();

        _slide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _meters.Start();
    }

    public void HidePanel()
    {
        if (!IsVisible)
        {
            return;
        }
        _meters.Stop();
        foreach (var row in _appRows.Values)
        {
            row.Collapse();
        }
        BeginAnimation(OpacityProperty, null);
        Hide();
        HiddenAt = DateTime.UtcNow;
    }

    // Bottom right of the screen with the cursor, above the taskbar (or
    // under it, when the taskbar is on top).
    void PlaceNearTray()
    {
        if (!IsVisible)
        {
            return;
        }
        var (scale, work) = Native.MonitorAtCursor();
        Native.GetCursorPos(out var cursor);
        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        double x = work.Right - w;
        double y = cursor.Y < (work.Top + work.Bottom) / 2 ? work.Top : work.Bottom - h;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, (int)x, (int)y, 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // ----------------------------------------------------------------- update

    // Brings the rows in line with the service: new rows for new devices and
    // programs, gone ones removed, values refreshed unless being dragged.
    public void Update()
    {
        var quick = _audio.QuickDevices();
        if (quick.Count >= 2)
        {
            var current = quick.FirstOrDefault(d => d.IsDefault);
            var next = quick[(quick.IndexOf(current!) + 1) % quick.Count];
            _switchText.Text = "на " + next.Label;
            _switchButton.Visibility = Visibility.Visible;
        }
        else
        {
            _switchButton.Visibility = Visibility.Collapsed;
        }

        Sync(_devices, _deviceRows, _audio.Devices, d => d.Id, d => new DeviceRow(d, this), (row, d) => row.Update(d));
        Sync(_apps, _appRows, _audio.Apps, a => a.Key, a => new AppRow(a, this), (row, a) => row.Update(a));
        _noApps.Visibility = _audio.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var keys = Settings.Current.Hotkeys;
        _footer.Text = $"{keys.SwitchOutput} — сменить вывод · {keys.AppVolumeUp} / {keys.AppVolumeDown} — " +
                       $"громкость активной программы · {keys.AppMute} — без звука";
    }

    static void Sync<TItem, TRow>(StackPanel panel, Dictionary<string, TRow> rows, IReadOnlyList<TItem> items,
        Func<TItem, string> key, Func<TItem, TRow> create, Action<TRow, TItem> update) where TRow : UIElement
    {
        var wanted = items.Select(key).ToList();
        foreach (var gone in rows.Keys.Except(wanted).ToList())
        {
            panel.Children.Remove(rows[gone]);
            rows.Remove(gone);
        }
        for (int i = 0; i < items.Count; i++)
        {
            string k = wanted[i];
            if (!rows.TryGetValue(k, out var row))
            {
                row = create(items[i]);
                rows[k] = row;
            }
            update(row, items[i]);
            int at = panel.Children.IndexOf(row);
            if (at != i)
            {
                if (at >= 0)
                {
                    panel.Children.RemoveAt(at);
                }
                panel.Children.Insert(i, row);
            }
        }
    }

    void UpdateMeters()
    {
        foreach (var d in _audio.Devices)
        {
            if (_deviceRows.TryGetValue(d.Id, out var row))
            {
                row.Bar.Peak = d.Peak;
            }
        }
        foreach (var a in _audio.Apps)
        {
            if (_appRows.TryGetValue(a.Key, out var row))
            {
                row.Bar.Peak = a.Peak;
            }
        }
    }

    internal void OnDefaultChosen(AudioDevice device)
    {
        if (!device.IsDefault)
        {
            _audio.SetDefault(device);
        }
        Update();
    }

    internal void OnQuickToggled(AudioDevice device)
    {
        var quick = Settings.Current.QuickDevices;
        if (!quick.Remove(device.Id))
        {
            quick.Add(device.Id);
        }
        Settings.Save();
        Update();
    }

    internal void OnRouteChosen(AudioApp app, string? deviceId)
    {
        _audio.Route(app, deviceId);
        Update();
    }

    internal AudioService Audio => _audio;

    // ----------------------------------------------------------- device row

    sealed class DeviceRow : Border
    {
        readonly PanelWindow _panel;
        AudioDevice _device;
        readonly TextBlock _icon;
        readonly TextBlock _name;
        readonly TextBlock _state;
        readonly TextBlock _star;
        readonly TextBlock _mute;
        readonly TextBlock _percent;
        public readonly VolumeBar Bar = new();

        public DeviceRow(AudioDevice device, PanelWindow panel)
        {
            _device = device;
            _panel = panel;
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(6, 6, 4, 4);
            Margin = new Thickness(0, 0, 0, 2);

            _icon = Theme.Glyph(Theme.DeviceGlyph(device.Kind), 18);
            _icon.Width = 28;
            _name = Theme.Label(device.Label, 13);
            _state = Theme.Label("", 11, Theme.Dim);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            names.Children.Add(_name);
            names.Children.Add(_state);

            var pick = new FlatButton(new DockPanel { Children = { _icon, names } },
                "Сделать устройством по умолчанию") { Padding = new Thickness(2) };
            pick.Click += () => _panel.OnDefaultChosen(_device);

            _star = Theme.Glyph("", 13, Theme.Dim);
            var star = new FlatButton(_star, "Участвует в быстром переключении (кнопка вверху и горячая клавиша)");
            star.Click += () => _panel.OnQuickToggled(_device);

            _mute = Theme.Glyph("", 14);
            var mute = new FlatButton(_mute, "Без звука");
            mute.Click += () =>
            {
                _device.Muted = !_device.Muted;
                Update(_device);
            };

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(star, 1);
            Grid.SetColumn(mute, 2);
            top.Children.Add(pick);
            top.Children.Add(star);
            top.Children.Add(mute);

            _percent = Theme.Label("", 11.5, Theme.Dim);
            _percent.Width = 34;
            _percent.TextAlignment = TextAlignment.Right;
            _percent.Margin = new Thickness(0, -4, 0, 0);
            Bar.UserChanged += v =>
            {
                _device.Volume = (float)v;
                if (_device.Muted && v > 0)
                {
                    _device.Muted = false;
                }
                Update(_device);
            };
            var bottom = new DockPanel { Margin = new Thickness(30, 2, 6, 0) };
            DockPanel.SetDock(_percent, Dock.Right);
            bottom.Children.Add(_percent);
            bottom.Children.Add(Bar);

            Child = new StackPanel { Children = { top, bottom } };
        }

        public void Update(AudioDevice device)
        {
            _device = device;
            bool quick = Settings.Current.QuickDevices.Contains(device.Id);
            Background = device.IsDefault ? Theme.AccentSoft : Brushes.Transparent;
            _icon.Foreground = device.IsDefault ? Theme.AccentBrush : Theme.Foreground;
            _name.Text = device.Label;
            _state.Text = device.IsDefault ? "по умолчанию" : (device.Adapter.Length > 0 && device.Label == device.Name ? device.Adapter : "");
            _state.Visibility = _state.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _star.Text = quick ? "" : "";
            _star.Foreground = quick ? Theme.AccentBrush : Theme.Faint;
            float volume = device.Volume;
            bool muted = device.Muted;
            _mute.Text = Theme.VolumeGlyph(volume, muted);
            _mute.Foreground = muted ? Theme.Danger : Theme.Foreground;
            if (!Bar.IsDragging)
            {
                Bar.Value = volume;
            }
            Bar.Muted = muted;
            _percent.Text = $"{Math.Round(volume * 100)}";
        }
    }

    // -------------------------------------------------------------- app row

    sealed class AppRow : Border
    {
        readonly PanelWindow _panel;
        AudioApp _app;
        readonly Image _image = new() { Width = 22, Height = 22 };
        readonly TextBlock _fallbackIcon = Theme.Glyph("", 16, Theme.Dim);
        readonly TextBlock _name;
        readonly TextBlock _routeText;
        readonly TextBlock _routeGlyph;
        readonly Border _route;
        readonly TextBlock _mute;
        readonly TextBlock _percent;
        readonly WrapPanel _choices = new() { Margin = new Thickness(30, 4, 0, 2) };
        public readonly VolumeBar Bar = new();

        public AppRow(AudioApp app, PanelWindow panel)
        {
            _app = app;
            _panel = panel;
            CornerRadius = new CornerRadius(8);
            Padding = new Thickness(6, 6, 4, 4);
            Margin = new Thickness(0, 0, 0, 2);
            Background = Brushes.Transparent;
            MouseEnter += (_, _) => Background = Theme.Brush(0x0A, 0xFF, 0xFF, 0xFF);
            MouseLeave += (_, _) => Background = Brushes.Transparent;

            var icon = new Grid { Width = 28, Children = { _fallbackIcon, _image } };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

            _name = Theme.Label(app.Name, 13);
            _name.Margin = new Thickness(6, 0, 8, 0);

            _routeGlyph = Theme.Glyph("", 11, Theme.Dim);
            _routeText = Theme.Label("", 11.5, Theme.Dim);
            _routeText.MaxWidth = 130;
            _routeText.Margin = new Thickness(5, 0, 4, 0);
            var routeContent = new StackPanel { Orientation = Orientation.Horizontal };
            routeContent.Children.Add(_routeGlyph);
            routeContent.Children.Add(_routeText);
            routeContent.Children.Add(Theme.Glyph("", 8, Theme.Dim));
            var routeButton = new FlatButton(routeContent, "Куда выводить звук этой программы")
            {
                Padding = new Thickness(7, 3, 7, 3),
                CornerRadius = new CornerRadius(10),
            };
            routeButton.Click += ToggleChoices;
            _route = new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                Child = routeButton,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _mute = Theme.Glyph("", 14);
            var mute = new FlatButton(_mute, "Без звука");
            mute.Click += () =>
            {
                _app.Muted = !_app.Muted;
                Update(_app);
            };

            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_name, 1);
            Grid.SetColumn(_route, 2);
            Grid.SetColumn(mute, 3);
            top.Children.Add(icon);
            top.Children.Add(_name);
            top.Children.Add(_route);
            top.Children.Add(mute);

            _percent = Theme.Label("", 11.5, Theme.Dim);
            _percent.Width = 34;
            _percent.TextAlignment = TextAlignment.Right;
            _percent.Margin = new Thickness(0, -4, 0, 0);
            Bar.UserChanged += v =>
            {
                _app.Volume = (float)v;
                if (_app.Muted && v > 0)
                {
                    _app.Muted = false;
                }
                Update(_app);
            };
            var bottom = new DockPanel { Margin = new Thickness(30, 2, 6, 0) };
            DockPanel.SetDock(_percent, Dock.Right);
            bottom.Children.Add(_percent);
            bottom.Children.Add(Bar);

            _choices.Visibility = Visibility.Collapsed;
            Child = new StackPanel { Children = { top, bottom, _choices } };
        }

        public void Update(AudioApp app)
        {
            _app = app;
            _name.Text = app.Name;
            var image = Theme.AppIcon(app.ExePath);
            _image.Source = image;
            _fallbackIcon.Text = app.IsSystem ? "" : "";
            _fallbackIcon.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
            _name.Foreground = app.IsPlaying ? Theme.Foreground : Theme.Dim;

            _route.Visibility = app.IsSystem ? Visibility.Collapsed : Visibility.Visible;
            var audio = _panel.Audio;
            if (app.RoutedDeviceId.Length == 0)
            {
                _routeText.Text = "По умолчанию";
                _routeGlyph.Text = Theme.DeviceGlyph(audio.Default?.Kind ?? DeviceKind.Speakers);
                _routeText.Foreground = Theme.Dim;
                _routeGlyph.Foreground = Theme.Dim;
                _route.BorderBrush = Theme.Brush(0x28, 0xFF, 0xFF, 0xFF);
            }
            else
            {
                var device = audio.Devices.FirstOrDefault(d => string.Equals(d.Id, app.RoutedDeviceId, StringComparison.OrdinalIgnoreCase));
                _routeText.Text = device?.Label ?? audio.DeviceLabel(app.RoutedDeviceId);
                _routeGlyph.Text = Theme.DeviceGlyph(device?.Kind ?? DeviceKind.Other);
                _routeText.Foreground = Theme.Foreground;
                _routeGlyph.Foreground = Theme.AccentBrush;
                _route.BorderBrush = Theme.AccentBrush;
            }

            float volume = app.Volume;
            bool muted = app.Muted;
            _mute.Text = Theme.VolumeGlyph(volume, muted);
            _mute.Foreground = muted ? Theme.Danger : Theme.Foreground;
            if (!Bar.IsDragging)
            {
                Bar.Value = volume;
            }
            Bar.Muted = muted;
            _percent.Text = $"{Math.Round(volume * 100)}";

            if (_choices.Visibility == Visibility.Visible)
            {
                BuildChoices();
            }
        }

        void ToggleChoices()
        {
            if (_choices.Visibility == Visibility.Visible)
            {
                Collapse();
                return;
            }
            BuildChoices();
            _choices.Visibility = Visibility.Visible;
        }

        public void Collapse() => _choices.Visibility = Visibility.Collapsed;

        void BuildChoices()
        {
            _choices.Children.Clear();
            var audio = _panel.Audio;
            _choices.Children.Add(Chip("По умолчанию", "", _app.RoutedDeviceId.Length == 0, null));
            foreach (var d in audio.Devices)
            {
                bool selected = string.Equals(d.Id, _app.RoutedDeviceId, StringComparison.OrdinalIgnoreCase);
                _choices.Children.Add(Chip(d.Label, Theme.DeviceGlyph(d.Kind), selected, d.Id));
            }
        }

        UIElement Chip(string text, string glyph, bool selected, string? deviceId)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Theme.Glyph(glyph, 11, selected ? Theme.AccentBrush : Theme.Dim));
            var label = Theme.Label(text, 11.5, selected ? Theme.Foreground : Theme.Dim);
            label.MaxWidth = 150;
            label.Margin = new Thickness(5, 0, 0, 0);
            content.Children.Add(label);
            var button = new FlatButton(content)
            {
                Padding = new Thickness(8, 4, 9, 4),
                CornerRadius = new CornerRadius(10),
            };
            button.Click += () =>
            {
                Collapse();
                _panel.OnRouteChosen(_app, deviceId);
            };
            return new Border
            {
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = selected ? Theme.AccentBrush : Theme.Brush(0x28, 0xFF, 0xFF, 0xFF),
                Background = selected ? Theme.AccentSoft : Brushes.Transparent,
                Margin = new Thickness(0, 0, 4, 4),
                Child = button,
            };
        }
    }

    // ------------------------------------------------------------- testing

    // Draws the panel into a PNG without showing it.
    public void RenderToPng(string path)
    {
        Update();
        _card.Effect = null;
        _card.Margin = new Thickness(0);
        _card.Measure(new Size(PanelWidth, double.PositiveInfinity));
        _card.Arrange(new Rect(_card.DesiredSize));
        _card.UpdateLayout();
        UpdateMeters();
        _card.UpdateLayout();
        double scale = 1.5;
        var bitmap = new RenderTargetBitmap((int)(_card.ActualWidth * scale), (int)(_card.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(_card);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
