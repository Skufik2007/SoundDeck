using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SoundDeck;

internal sealed class Tray : IDisposable
{
    readonly NotifyIcon _icon;
    readonly ToolStripMenuItem _hotkeyProblems;

    public Tray(Action togglePanel, Action switchOutput, Action resetRoutes, Func<IReadOnlyList<string>> failedHotkeys, Action exit)
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem($"Открыть пульт ({Settings.Current.Hotkeys.OpenPanel})") { Font = new Font(menu.Font, FontStyle.Bold) };
        open.Click += (_, _) => togglePanel();
        menu.Items.Add(open);

        var swap = new ToolStripMenuItem($"Переключить вывод ({Settings.Current.Hotkeys.SwitchOutput})");
        swap.Click += (_, _) => switchOutput();
        menu.Items.Add(swap);

        _hotkeyProblems = new ToolStripMenuItem { Enabled = false, Visible = false };
        menu.Items.Add(_hotkeyProblems);

        menu.Items.Add(new ToolStripSeparator());

        var reset = new ToolStripMenuItem("Вернуть все программы на устройство по умолчанию");
        reset.Click += (_, _) => resetRoutes();
        menu.Items.Add(reset);

        var mixer = new ToolStripMenuItem("Микшер громкости Windows");
        mixer.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:apps-volume") { UseShellExecute = true });
        menu.Items.Add(mixer);

        menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            Checked = Autostart.IsEnabled,
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => Autostart.Set(autostart.Checked);
        menu.Items.Add(autostart);

        var openFolder = new ToolStripMenuItem("Папка настроек (горячие клавиши в settings.json)");
        openFolder.Click += (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.Folder}\"") { UseShellExecute = true });
        menu.Items.Add(openFolder);

        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("Выход");
        quit.Click += (_, _) => exit();
        menu.Items.Add(quit);

        menu.Opening += (_, _) =>
        {
            var failed = failedHotkeys();
            _hotkeyProblems.Visible = failed.Count > 0;
            _hotkeyProblems.Text = "Заняты другой программой: " + string.Join(", ", failed);
        };

        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "Звуковой пульт",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                togglePanel();
            }
        };
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon allows 127 characters.
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    // Three mixer faders, the middle knob in the accent color.
    static Icon DrawIcon()
    {
        var accent = Theme.Accent;
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var line = new Pen(System.Drawing.Color.FromArgb(230, 235, 235, 240), 3) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            int[] knobs = { 20, 9, 16 };
            for (int i = 0; i < 3; i++)
            {
                int x = 6 + i * 10;
                g.DrawLine(line, x, 4, x, 28);
                var color = i == 1
                    ? System.Drawing.Color.FromArgb(255, accent.R, accent.G, accent.B)
                    : System.Drawing.Color.FromArgb(255, 245, 245, 248);
                using var knob = new SolidBrush(color);
                using var rim = new Pen(System.Drawing.Color.FromArgb(255, 32, 34, 39), 1.5f);
                g.FillEllipse(knob, x - 5, knobs[i] - 5, 10, 10);
                g.DrawEllipse(rim, x - 5, knobs[i] - 5, 10, 10);
            }
        }
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
