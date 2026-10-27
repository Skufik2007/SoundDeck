using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace SoundDeck;

internal static class Theme
{
    public static Color Accent { get; } = ReadAccent();

    static Color ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
            }
        }
        catch
        {
        }
        return Color.FromRgb(0x4C, 0xC2, 0xFF);
    }

    public static readonly FontFamily Text = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly SolidColorBrush Card = Brush(0xF5, 0x1F, 0x21, 0x26);
    public static readonly SolidColorBrush CardBorder = Brush(0x38, 0xFF, 0xFF, 0xFF);
    public static readonly SolidColorBrush Foreground = Brush(0xFF, 0xF4, 0xF4, 0xF6);
    public static readonly SolidColorBrush Dim = Brush(0x99, 0xF4, 0xF4, 0xF6);
    public static readonly SolidColorBrush Faint = Brush(0x66, 0xF4, 0xF4, 0xF6);
    public static readonly SolidColorBrush Hover = Brush(0x14, 0xFF, 0xFF, 0xFF);
    public static readonly SolidColorBrush Pressed = Brush(0x22, 0xFF, 0xFF, 0xFF);
    public static readonly SolidColorBrush AccentBrush = Brush(Accent);
    public static readonly SolidColorBrush AccentSoft = Brush(Color.FromArgb(0x40, Accent.R, Accent.G, Accent.B));
    public static readonly SolidColorBrush Danger = Brush(0xFF, 0xFF, 0x6B, 0x6B);

    public static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => Brush(Color.FromArgb(a, r, g, b));

    public static string DeviceGlyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Headphones => "",
        DeviceKind.Headset => "",
        DeviceKind.Display => "",
        _ => "",
    };

    public static string VolumeGlyph(float volume, bool muted) =>
        muted || volume <= 0.001f ? "" :
        volume < 0.34f ? "" :
        volume < 0.67f ? "" : "";

    public static TextBlock Glyph(string glyph, double size, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = Icons,
        FontSize = size,
        Foreground = brush ?? Foreground,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static TextBlock Label(string text, double size, Brush? brush = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontFamily = Text,
        FontSize = size,
        Foreground = brush ?? Foreground,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static readonly Dictionary<string, ImageSource?> AppIcons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? AppIcon(string? exePath)
    {
        if (exePath == null)
        {
            return null;
        }
        if (AppIcons.TryGetValue(exePath, out var cached))
        {
            return cached;
        }
        ImageSource? image = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
            {
                image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
            }
        }
        catch
        {
        }
        AppIcons[exePath] = image;
        return image;
    }
}

// A flat button: a glyph or any content with a hover background.
internal sealed class FlatButton : Border
{
    public event Action? Click;
    bool _pressed;

    public FlatButton(UIElement content, string? tooltip = null)
    {
        Child = content;
        CornerRadius = new CornerRadius(6);
        Background = Brushes.Transparent;
        Padding = new Thickness(6);
        Cursor = Cursors.Hand;
        ToolTip = tooltip;
        MouseEnter += (_, _) => Background = Theme.Hover;
        MouseLeave += (_, _) =>
        {
            Background = Brushes.Transparent;
            _pressed = false;
        };
        MouseLeftButtonDown += (_, e) =>
        {
            _pressed = true;
            Background = Theme.Pressed;
            e.Handled = true;
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed)
            {
                _pressed = false;
                Background = Theme.Hover;
                e.Handled = true;
                Click?.Invoke();
            }
        };
    }
}

// The volume slider with a level meter under it, drawn by hand so it fits
// the dark panel. Drag, click or scroll the wheel over it.
internal sealed class VolumeBar : FrameworkElement
{
    double _value;
    double _peak;
    bool _muted;
    bool _dragging;

    public event Action<double>? UserChanged;

    public bool Interactive { get; init; } = true;
    public double WheelStep { get; init; } = 0.02;
    public bool IsDragging => _dragging;

    public VolumeBar()
    {
        Height = 22;
        Cursor = Cursors.Hand;
        Focusable = false;
    }

    public double Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _value) > 0.0005)
            {
                _value = value;
                InvalidateVisual();
            }
        }
    }

    public double Peak
    {
        get => _peak;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(value - _peak) > 0.004)
            {
                _peak = value;
                InvalidateVisual();
            }
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (value != _muted)
            {
                _muted = value;
                InvalidateVisual();
            }
        }
    }

    const double Thumb = 7;

    static readonly Brush Track = Theme.Brush(0x38, 0xFF, 0xFF, 0xFF);
    static readonly Brush MutedFill = Theme.Brush(0x70, 0xFF, 0xFF, 0xFF);
    static readonly Brush PeakFill = Theme.Brush(0xB0, 0xFF, 0xFF, 0xFF);
    static readonly Brush ThumbFill = Theme.Brush(0xFF, 0xEE, 0xEE, 0xF0);
    static readonly Pen ThumbPen = new(Theme.Brush(0x40, 0x00, 0x00, 0x00), 1);

    protected override void OnRender(DrawingContext dc)
    {
        double left = Thumb;
        double width = Math.Max(0, ActualWidth - Thumb * 2);
        double mid = Math.Round(ActualHeight / 2) - 2;

        // Hit area, so clicks between the lines count.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        dc.DrawRoundedRectangle(Track, null, new Rect(left, mid - 2, width, 4), 2, 2);
        double fill = width * _value;
        if (fill > 0)
        {
            dc.DrawRoundedRectangle(_muted ? MutedFill : Theme.AccentBrush, null, new Rect(left, mid - 2, fill, 4), 2, 2);
        }

        // What's actually coming out, under the track.
        double peak = _muted ? 0 : width * _peak * _value;
        if (peak > 1)
        {
            dc.DrawRoundedRectangle(PeakFill, null, new Rect(left, mid + 5, peak, 2), 1, 1);
        }

        if (Interactive)
        {
            var center = new Point(left + fill, mid);
            dc.DrawEllipse(ThumbFill, ThumbPen, center, Thumb, Thumb);
            dc.DrawEllipse(_muted ? MutedFill : Theme.AccentBrush, null, center, _dragging ? 4.5 : 3.5, _dragging ? 4.5 : 3.5);
        }
    }

    void SetFromPoint(Point p)
    {
        double width = Math.Max(1, ActualWidth - Thumb * 2);
        Value = (p.X - Thumb) / width;
        UserChanged?.Invoke(_value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!Interactive)
        {
            return;
        }
        _dragging = CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging)
        {
            SetFromPoint(e.GetPosition(this));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _dragging = false;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!Interactive)
        {
            return;
        }
        Value = Math.Round((_value + Math.Sign(e.Delta) * WheelStep) * 100) / 100;
        UserChanged?.Invoke(_value);
        e.Handled = true;
    }
}
