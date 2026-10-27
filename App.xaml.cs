using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SoundDeck.Interop;

namespace SoundDeck;

public partial class App : Application
{
    Mutex? _singleInstance;
    AudioService? _audio;
    PanelWindow? _panel;
    OsdWindow? _osd;
    Tray? _tray;
    Hotkeys? _hotkeys;
    DispatcherTimer? _refresh;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings.Load();

        bool restarted = e.Args.Contains("--restarted");
        if (e.Args.Length > 0 && e.Args[0].StartsWith("--", StringComparison.Ordinal) && !restarted)
        {
            int code = RunCommandLine(e.Args);
            Shutdown(code);
            return;
        }

        // No "has stopped working" dialog: it would keep a crashed instance
        // alive and the replacement waiting for it.
        Native.WerSetFlags(Native.WER_FAULT_REPORTING_NO_UI);

        _singleInstance = new Mutex(false, "SoundDeck.SingleInstance");
        bool owned;
        try
        {
            owned = _singleInstance.WaitOne(restarted ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }
        if (!owned)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("Unhandled: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Write("Unobserved: " + args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Write("Fatal: " + args.ExceptionObject);
            RestartAfterCrash();
        };

        // The program may have been moved, or set up with an older version.
        System.Threading.Tasks.Task.Run(Autostart.Repair);

        _audio = new AudioService();
        _audio.Refresh();
        if (!Settings.Current.QuickDevicesGuessed)
        {
            _audio.GuessQuickDevices();
            Settings.Current.QuickDevicesGuessed = true;
            Settings.Save();
        }
        _panel = new PanelWindow(_audio);
        _osd = new OsdWindow();

        _hotkeys = new Hotkeys();
        var keys = Settings.Current.Hotkeys;
        _hotkeys.Register(keys.SwitchOutput, SwitchOutput, repeat: false);
        _hotkeys.Register(keys.AppVolumeUp, () => ChangeForegroundVolume(+1), repeat: true);
        _hotkeys.Register(keys.AppVolumeDown, () => ChangeForegroundVolume(-1), repeat: true);
        _hotkeys.Register(keys.AppMute, ToggleForegroundMute, repeat: false);
        _hotkeys.Register(keys.OpenPanel, () => _panel.Toggle(), repeat: false);

        _tray = new Tray(() => _panel.Toggle(), SwitchOutput, ResetRoutes, () => _hotkeys.Failed, Shutdown);

        // Keeps the panel live, and puts saved routes back on programs that
        // start making sound.
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Interval = TimeSpan.FromSeconds(_panel.IsVisible ? 1 : 2.5);
            _audio.Refresh();
            if (_panel.IsVisible)
            {
                _panel.Update();
            }
            UpdateTooltip();
        };
        _refresh.Start();
        UpdateTooltip();

        if (!AudioPolicyConfig.IsAvailable)
        {
            Log.Write("Per-app routing unavailable: " + AudioPolicyConfig.LoadError);
        }
        Log.Write($"Started; {_audio.Devices.Count} devices, {_audio.Apps.Count} apps, hotkeys failed: {string.Join(", ", _hotkeys.Failed)}");

        if (!File.Exists(Path.Combine(Settings.Folder, "welcomed")))
        {
            File.WriteAllText(Path.Combine(Settings.Folder, "welcomed"), "");
            _panel.ShowPanel();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance != null)
        {
            Log.Write("Exited");
        }
        _refresh?.Stop();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        if (_singleInstance != null)
        {
            try
            {
                _singleInstance.ReleaseMutex();
            }
            catch
            {
            }
            _singleInstance.Dispose();
        }
        base.OnExit(e);
    }

    void UpdateTooltip()
    {
        var device = _audio?.Default;
        _tray?.SetTooltip(device == null ? "Звуковой пульт" : "Звуковой пульт · " + device.Label);
    }

    // ---------------------------------------------------------------- actions

    void SwitchOutput()
    {
        var audio = _audio!;
        audio.Refresh();
        var next = audio.SwitchToNext();
        if (next != null)
        {
            _osd!.ShowVolume(next.Label, "Теперь звук идёт сюда", null, Theme.DeviceGlyph(next.Kind), next.Volume, next.Muted);
        }
        else
        {
            _osd!.ShowVolume("Не на что переключать", "Подключено одно устройство вывода", null, "", null, false);
        }
        UpdateTooltip();
        if (_panel!.IsVisible)
        {
            _panel.Update();
        }
    }

    void ChangeForegroundVolume(int direction)
    {
        var app = ForegroundApp(out string name);
        if (app == null)
        {
            _osd!.ShowVolume(name, "Эта программа сейчас не работает со звуком", null, "", null, false);
            return;
        }
        float step = Settings.Current.VolumeStepPercent / 100f;
        float volume = Math.Clamp(MathF.Round((app.Volume + direction * step) / step) * step, 0, 1);
        app.Volume = volume;
        if (direction > 0 && app.Muted)
        {
            app.Muted = false;
        }
        ShowAppOsd(app);
    }

    void ToggleForegroundMute()
    {
        var app = ForegroundApp(out string name);
        if (app == null)
        {
            _osd!.ShowVolume(name, "Эта программа сейчас не работает со звуком", null, "", null, false);
            return;
        }
        app.Muted = !app.Muted;
        ShowAppOsd(app);
    }

    void ShowAppOsd(AudioApp app)
    {
        string where = app.RoutedDeviceId.Length > 0
            ? _audio!.DeviceLabel(app.RoutedDeviceId)
            : _audio!.Default?.Label ?? "";
        _osd!.ShowVolume(app.Name, where, Theme.AppIcon(app.ExePath), "", app.Volume, app.Muted);
        if (_panel!.IsVisible)
        {
            _panel.Update();
        }
    }

    AudioApp? ForegroundApp(out string name)
    {
        uint pid = Native.ForegroundProcessId();
        string? path = Native.ImagePath(pid);
        name = path != null ? Path.GetFileNameWithoutExtension(path) : "Программа";
        _audio!.Refresh();
        return pid == 0 ? null : _audio.AppOfProcess(pid);
    }

    void ResetRoutes()
    {
        int hr = AudioPolicyConfig.ClearAll();
        Settings.Current.Routes.Clear();
        Settings.Save();
        Log.Write($"Routes cleared: 0x{hr:X8}");
        _audio!.Refresh();
        _osd!.ShowVolume("Все программы — на устройство по умолчанию", "", null, "", null, false);
        if (_panel!.IsVisible)
        {
            _panel.Update();
        }
    }

    // Brings the program back after a crash, but gives up after three crashes
    // in ten minutes rather than looping.
    static void RestartAfterCrash()
    {
        try
        {
            string file = Path.Combine(Settings.Folder, "restarts.txt");
            DateTime now = DateTime.UtcNow;
            var recent = new List<DateTime>();
            if (File.Exists(file))
            {
                foreach (string line in File.ReadAllLines(file))
                {
                    if (DateTime.TryParse(line, null, System.Globalization.DateTimeStyles.RoundtripKind, out var time) &&
                        now - time < TimeSpan.FromMinutes(10))
                    {
                        recent.Add(time);
                    }
                }
            }
            if (recent.Count >= 3)
            {
                Log.Write("Crashed 3 times in 10 minutes, not restarting");
                return;
            }
            recent.Add(now);
            File.WriteAllLines(file, recent.Select(t => t.ToString("o")));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restarted")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log.Write("Restart after crash failed: " + ex.Message);
        }
    }

    // ---------------------------------------------------------- command line

    // For checking without clicking:
    //   --list <report>                       devices, programs, routes
    //   --route <exe> <device|default> <report>
    //   --default <device> <report>
    //   --render <out.png>                    the panel as a picture
    //   --hotkeys <report>                    which hotkeys can be taken
    int RunCommandLine(string[] args)
    {
        try
        {
            var audio = new AudioService();
            audio.Refresh();

            AudioDevice? FindDevice(string text) =>
                audio.Devices.FirstOrDefault(d => d.Label.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                                                  d.Adapter.Contains(text, StringComparison.CurrentCultureIgnoreCase) ||
                                                  d.Id == text);

            switch (args[0])
            {
                case "--list":
                    File.WriteAllLines(args[1], Describe(audio));
                    return 0;

                case "--route":
                {
                    var app = audio.Apps.FirstOrDefault(a => a.Key.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                    if (app == null)
                    {
                        File.WriteAllText(args[3], "NO APP " + args[1]);
                        return 1;
                    }
                    string? id = args[2] == "default" ? null : FindDevice(args[2])?.Id;
                    audio.Route(app, id);
                    Thread.Sleep(800);
                    audio.Refresh();
                    File.WriteAllLines(args[3], Describe(audio));
                    return 0;
                }

                case "--default":
                {
                    var device = FindDevice(args[1]);
                    bool ok = device != null && audio.SetDefault(device);
                    audio.Refresh();
                    File.WriteAllLines(args[2], new[] { ok ? "OK" : "FAILED" }.Concat(Describe(audio)));
                    return ok ? 0 : 1;
                }

                case "--render":
                    new PanelWindow(audio).RenderToPng(args[1]);
                    return 0;

                case "--hotkeys":
                {
                    using var hotkeys = new Hotkeys();
                    var keys = Settings.Current.Hotkeys;
                    var lines = new List<string>();
                    foreach (string spec in new[] { keys.SwitchOutput, keys.AppVolumeUp, keys.AppVolumeDown, keys.AppMute, keys.OpenPanel })
                    {
                        lines.Add($"{spec}: {(hotkeys.Register(spec, () => { }, true) ? "OK" : "TAKEN")}");
                    }
                    File.WriteAllLines(args[1], lines);
                    return 0;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Command line failed: " + ex);
            return 2;
        }
        return 3;
    }

    static IEnumerable<string> Describe(AudioService audio)
    {
        yield return "per-app routing: " + (AudioPolicyConfig.IsAvailable ? "yes" : "no — " + AudioPolicyConfig.LoadError);
        foreach (var d in audio.Devices)
        {
            yield return $"DEVICE {(d.IsDefault ? "*" : " ")} {d.Label} | {d.Adapter} | {d.Kind} | vol {d.Volume:0.00}{(d.Muted ? " muted" : "")} | {d.Id}";
        }
        foreach (var a in audio.Apps)
        {
            string route = a.RoutedDeviceId.Length == 0 ? "default" : audio.DeviceLabel(a.RoutedDeviceId);
            yield return $"APP {a.Key} | {a.Name} | vol {a.Volume:0.00}{(a.Muted ? " muted" : "")} | " +
                         $"{(a.IsPlaying ? "playing" : "idle")} | pids {string.Join(",", a.ProcessIds)} | " +
                         $"sessions {a.Sessions.Count} on {string.Join(",", a.Sessions.Select(s => audio.DeviceLabel(s.DeviceId)).Distinct())} | → {route}";
        }
    }
}
