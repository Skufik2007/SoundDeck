using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace SoundDeck;

// The little pill at the bottom of the screen after a hotkey: which program
// or device and how loud. Never takes focus, the mouse goes through it.
internal sealed class OsdWindow : Window
{
    readonly Image _image = new() { Width = 26, Height = 26 };
    readonly TextBlock _glyph = Theme.Glyph("", 22);
    readonly TextBlock _title = Theme.Label("", 13.5, weight: FontWeights.SemiBold);
    readonly TextBlock _subtitle = Theme.Label("", 11.5, Theme.Dim);
    readonly TextBlock _percent = Theme.Label("", 13, Theme.Dim);
    readonly VolumeBar _bar = new() { Interactive = false, Height = 14 };
    readonly DispatcherTimer _hide;

    public OsdWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        Title = "SoundDeck OSD";
        IsHitTestVisible = false;

        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var icon = new Grid { Width = 36, Children = { _glyph, _image } };

        _percent.Width = 40;
        _percent.TextAlignment = TextAlignment.Right;
        _subtitle.Margin = new Thickness(0, 1, 0, 0);

        var barRow = new DockPanel { Margin = new Thickness(-7, 4, 0, 0) };
        DockPanel.SetDock(_percent, Dock.Right);
        barRow.Children.Add(_percent);
        barRow.Children.Add(_bar);

        var texts = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(_title);
        texts.Children.Add(_subtitle);
        texts.Children.Add(barRow);

        var row = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(texts);

        Content = new Border
        {
            Background = Theme.Card,
            BorderBrush = Theme.CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 16, 10),
            Margin = new Thickness(10),
            Child = row,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.45,
                Color = Colors.Black,
            },
        };
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (_, _) =>
            {
                if (!_hide.IsEnabled)
                {
                    Hide();
                }
            };
            BeginAnimation(OpacityProperty, fade);
        };
        SourceInitialized += (_, _) => Native.MakeNoActivate(this, clickThrough: true);
    }

    // volume null: no bar, just the message.
    public void ShowVolume(string title, string subtitle, ImageSource? image, string glyph, double? volume, bool muted)
    {
        _title.Text = title;
        _subtitle.Text = subtitle;
        _subtitle.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _image.Source = image;
        _glyph.Text = image == null ? glyph : "";
        _glyph.Foreground = muted ? Theme.Danger : Theme.AccentBrush;

        var barRow = (FrameworkElement)_bar.Parent;
        barRow.Visibility = volume.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (volume.HasValue)
        {
            _bar.Value = volume.Value;
            _bar.Peak = 0;
            _bar.Muted = muted;
            _percent.Text = muted ? "выкл" : $"{Math.Round(volume.Value * 100)}%";
        }

        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible)
        {
            Show();
        }
        UpdateLayout();
        Place();
        _hide.Stop();
        _hide.Start();
    }

    // Bottom center of the monitor with the foreground window.
    void Place()
    {
        var (scale, work) = Native.MonitorOfForeground();
        double w = ActualWidth * scale;
        double h = ActualHeight * scale;
        int x = (int)((work.Left + work.Right) / 2 - w / 2);
        int y = (int)(work.Bottom - h - 60 * scale);
        SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
