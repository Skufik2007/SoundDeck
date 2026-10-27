using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SoundDeck.Interop;

namespace SoundDeck;

internal enum DeviceKind
{
    Speakers,
    Headphones,
    Headset,
    Display,
    Other,
}

// An output device: speakers, headphones, a monitor over HDMI…
internal sealed class AudioDevice
{
    public required string Id { get; init; }
    // "Наушники", "Динамики" — what the device is.
    public required string Name { get; init; }
    // "Realtek(R) Audio", "AirPods" — what it's connected through.
    public required string Adapter { get; init; }
    public required DeviceKind Kind { get; init; }
    public bool IsDefault { get; set; }

    // Name, or "Name (Adapter)" when another device has the same name.
    public string Label { get; set; } = "";

    internal IMMDevice? Device;
    internal IAudioEndpointVolume? Endpoint;
    internal IAudioMeterInformation? Meter;
    internal IAudioSessionManager2? Sessions;

    static Guid _context = Guid.Empty;

    public float Volume
    {
        get => Endpoint != null && Endpoint.GetMasterVolumeLevelScalar(out float v) >= 0 ? v : 0;
        set => Endpoint?.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 1), ref _context);
    }

    public bool Muted
    {
        get => Endpoint != null && Endpoint.GetMute(out bool m) >= 0 && m;
        set => Endpoint?.SetMute(value, ref _context);
    }

    public float Peak => Meter != null && Meter.GetPeakValue(out float p) >= 0 ? p : 0;
}

internal sealed class AppSession
{
    public required string InstanceId { get; init; }
    public required uint ProcessId { get; init; }
    public required string DeviceId { get; init; }
    internal required IAudioSessionControl2 Control { get; init; }
    internal required ISimpleAudioVolume Volume { get; init; }
    internal IAudioMeterInformation? Meter { get; init; }
    public bool Seen;
}

// One program with all its sound streams, across its processes (a browser
// has many) and devices.
internal sealed class AudioApp
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? ExePath { get; init; }
    public bool IsSystem { get; init; }
    public List<AppSession> Sessions { get; } = new();
    public HashSet<uint> ProcessIds { get; } = new();
    public bool IsPlaying { get; set; }

    // "" — follows the default device.
    public string RoutedDeviceId { get; set; } = "";

    static Guid _context = Guid.Empty;

    public float Volume
    {
        get
        {
            foreach (var s in Sessions)
            {
                if (s.Volume.GetMasterVolume(out float v) >= 0)
                {
                    return v;
                }
            }
            return 0;
        }
        set
        {
            float level = Math.Clamp(value, 0, 1);
            foreach (var s in Sessions)
            {
                s.Volume.SetMasterVolume(level, ref _context);
            }
        }
    }

    public bool Muted
    {
        get => Sessions.Count > 0 && Sessions[0].Volume.GetMute(out bool m) >= 0 && m;
        set
        {
            foreach (var s in Sessions)
            {
                s.Volume.SetMute(value, ref _context);
            }
        }
    }

    public float Peak
    {
        get
        {
            float max = 0;
            foreach (var s in Sessions)
            {
                if (s.Meter != null && s.Meter.GetPeakValue(out float p) >= 0 && p > max)
                {
                    max = p;
                }
            }
            return max;
        }
    }
}

// Everything the panel and the hotkeys need from Windows audio. Lives on
// the UI thread; Refresh() is called on a timer.
internal sealed class AudioService
{
    readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
    readonly Dictionary<string, AppSession> _sessions = new();
    readonly Dictionary<uint, ProcessInfo?> _processes = new();
    readonly HashSet<string> _routeChecked = new();

    public List<AudioDevice> Devices { get; private set; } = new();
    public List<AudioApp> Apps { get; private set; } = new();
    public AudioDevice? Default => Devices.FirstOrDefault(d => d.IsDefault);

    // Raised after a refresh in which devices came or went.
    public event Action? DevicesChanged;

    sealed record ProcessInfo(string Key, string Name, string? Path);

    public void Refresh()
    {
        RefreshDevices();
        RefreshSessions();
        ApplyRoutes();
    }

    // ---------------------------------------------------------------- devices

    void RefreshDevices()
    {
        var ids = new List<string>();
        if (_enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var collection) >= 0)
        {
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) >= 0 && device.GetId(out string id) >= 0)
                {
                    ids.Add(id);
                }
            }
            Marshal.ReleaseComObject(collection);
        }

        string defaultId = "";
        if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var def) >= 0)
        {
            def.GetId(out defaultId);
            Marshal.ReleaseComObject(def);
        }

        if (!ids.SequenceEqual(Devices.Select(d => d.Id)))
        {
            var fresh = new List<AudioDevice>();
            foreach (string id in ids)
            {
                var existing = Devices.FirstOrDefault(d => d.Id == id);
                var device = existing ?? OpenDevice(id);
                if (device != null)
                {
                    fresh.Add(device);
                }
            }
            Devices = fresh;
            foreach (var d in Devices)
            {
                d.Label = Devices.Count(o => o.Name == d.Name) > 1 && d.Adapter.Length > 0
                    ? $"{d.Name} ({d.Adapter})"
                    : d.Name;
            }
            // Sessions of a device that went away are gone with it.
            foreach (var key in _sessions.Where(p => !ids.Contains(p.Value.DeviceId)).Select(p => p.Key).ToList())
            {
                _sessions.Remove(key);
            }
            _routeChecked.Clear();
            DevicesChanged?.Invoke();
        }

        foreach (var d in Devices)
        {
            d.IsDefault = d.Id == defaultId;
        }
    }

    AudioDevice? OpenDevice(string id)
    {
        if (_enumerator.GetDevice(id, out var device) < 0)
        {
            return null;
        }
        string name = "", adapter = "", friendly = "";
        uint formFactor = 0;
        if (device.OpenPropertyStore(0, out var props) >= 0)
        {
            name = ReadString(props, PropertyKey.DeviceDescription) ?? "";
            adapter = ReadString(props, PropertyKey.InterfaceFriendlyName) ?? "";
            friendly = ReadString(props, PropertyKey.DeviceFriendlyName) ?? "";
            formFactor = ReadUInt(props, PropertyKey.EndpointFormFactor) ?? 0;
            Marshal.ReleaseComObject(props);
        }
        if (name.Length == 0)
        {
            name = friendly.Length > 0 ? friendly : "Устройство";
        }

        var kind = formFactor switch
        {
            1 => DeviceKind.Speakers,
            3 => DeviceKind.Headphones,
            5 => DeviceKind.Headset,
            9 => DeviceKind.Display,
            _ => DeviceKind.Other,
        };
        // Bluetooth headphones often say "Headphones" only in the name.
        if (kind == DeviceKind.Other)
        {
            string text = (name + " " + adapter).ToLowerInvariant();
            if (text.Contains("наушник") || text.Contains("headphone") || text.Contains("buds") || text.Contains("airpods"))
            {
                kind = DeviceKind.Headphones;
            }
            else if (text.Contains("динамик") || text.Contains("speaker"))
            {
                kind = DeviceKind.Speakers;
            }
        }

        var result = new AudioDevice { Id = id, Name = name, Adapter = adapter, Kind = kind, Device = device };
        if (device.Activate(ref AudioIids.EndpointVolume, AudioIids.ClsCtxAll, IntPtr.Zero, out object endpoint) >= 0)
        {
            result.Endpoint = (IAudioEndpointVolume)endpoint;
        }
        if (device.Activate(ref AudioIids.MeterInformation, AudioIids.ClsCtxAll, IntPtr.Zero, out object meter) >= 0)
        {
            result.Meter = (IAudioMeterInformation)meter;
        }
        if (device.Activate(ref AudioIids.SessionManager2, AudioIids.ClsCtxAll, IntPtr.Zero, out object sessions) >= 0)
        {
            result.Sessions = (IAudioSessionManager2)sessions;
        }
        return result;
    }

    static string? ReadString(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var value) < 0)
        {
            return null;
        }
        string? text = value.AsString();
        PropVariant.PropVariantClear(ref value);
        return text;
    }

    static uint? ReadUInt(IPropertyStore props, PropertyKey key)
    {
        if (props.GetValue(ref key, out var value) < 0)
        {
            return null;
        }
        uint? number = value.AsUInt();
        PropVariant.PropVariantClear(ref value);
        return number;
    }

    // --------------------------------------------------------------- sessions

    void RefreshSessions()
    {
        foreach (var s in _sessions.Values)
        {
            s.Seen = false;
        }

        foreach (var device in Devices)
        {
            if (device.Sessions == null || device.Sessions.GetSessionEnumerator(out var list) < 0)
            {
                continue;
            }
            list.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                if (list.GetSession(i, out object raw) < 0 || raw is not IAudioSessionControl2 control)
                {
                    continue;
                }
                if (control.GetState(out var state) < 0 || state == AudioSessionState.Expired ||
                    control.GetSessionInstanceIdentifier(out string instance) < 0)
                {
                    continue;
                }
                string key = device.Id + "|" + instance;
                if (_sessions.TryGetValue(key, out var known))
                {
                    known.Seen = true;
                    continue;
                }
                control.GetProcessId(out uint pid);
                if (raw is not ISimpleAudioVolume volume)
                {
                    continue;
                }
                _sessions[key] = new AppSession
                {
                    InstanceId = instance,
                    ProcessId = control.IsSystemSoundsSession() == 0 ? 0 : pid,
                    DeviceId = device.Id,
                    Control = control,
                    Volume = volume,
                    Meter = raw as IAudioMeterInformation,
                    Seen = true,
                };
            }
            Marshal.ReleaseComObject(list);
        }

        foreach (var key in _sessions.Where(p => !p.Value.Seen).Select(p => p.Key).ToList())
        {
            _sessions.Remove(key);
        }

        // Group the streams by program.
        var apps = new Dictionary<string, AudioApp>();
        foreach (var session in _sessions.Values)
        {
            ProcessInfo? info = session.ProcessId == 0
                ? new ProcessInfo("#system", "Системные звуки", null)
                : Describe(session);
            if (info == null)
            {
                continue; // the process has exited
            }
            if (!apps.TryGetValue(info.Key, out var app))
            {
                app = new AudioApp { Key = info.Key, Name = info.Name, ExePath = info.Path, IsSystem = session.ProcessId == 0 };
                apps[info.Key] = app;
            }
            app.Sessions.Add(session);
            if (session.ProcessId != 0)
            {
                app.ProcessIds.Add(session.ProcessId);
            }
            if (session.Control.GetState(out var state) >= 0 && state == AudioSessionState.Active)
            {
                app.IsPlaying = true;
            }
        }

        foreach (var app in apps.Values)
        {
            // Streams on the default device first: their volume is what the
            // user hears.
            app.Sessions.Sort((a, b) => IsDefault(b.DeviceId).CompareTo(IsDefault(a.DeviceId)));
            if (!app.IsSystem && app.ProcessIds.Count > 0)
            {
                app.RoutedDeviceId = AudioPolicyConfig.GetForProcess(app.ProcessIds.First());
            }
        }

        // By name, so rows don't jump around as programs start and stop
        // playing; system sounds last.
        Apps = apps.Values
            .OrderBy(a => a.IsSystem)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Forget processes that are gone.
        var live = _sessions.Values.Select(s => s.ProcessId).ToHashSet();
        foreach (uint pid in _processes.Keys.Where(p => !live.Contains(p)).ToList())
        {
            _processes.Remove(pid);
        }
    }

    bool IsDefault(string deviceId) => Default?.Id == deviceId;

    ProcessInfo? Describe(AppSession session)
    {
        uint pid = session.ProcessId;
        if (_processes.TryGetValue(pid, out var cached))
        {
            return cached;
        }
        ProcessInfo? info = null;
        string? path = Native.ImagePath(pid);
        if (path != null)
        {
            string exe = System.IO.Path.GetFileNameWithoutExtension(path);
            string name = "";
            try
            {
                name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim() ?? "";
            }
            catch
            {
            }
            if (name.Length == 0 || name.Length > 40)
            {
                session.Control.GetDisplayName(out string display);
                name = !string.IsNullOrWhiteSpace(display) && !display.StartsWith('@')
                    ? display
                    : char.ToUpperInvariant(exe[0]) + exe[1..];
            }
            info = new ProcessInfo(exe.ToLowerInvariant(), name, path);
        }
        _processes[pid] = info;
        return info;
    }

    public AudioApp? AppOfProcess(uint pid)
    {
        var app = Apps.FirstOrDefault(a => a.ProcessIds.Contains(pid));
        if (app != null)
        {
            return app;
        }
        // Another process of the same program may be the one making sound
        // (browsers, Discord, games with launchers).
        string? path = Native.ImagePath(pid);
        if (path == null)
        {
            return null;
        }
        string key = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return Apps.FirstOrDefault(a => a.Key == key);
    }

    // ---------------------------------------------------------------- routing

    // Sends the program to a device; null or "" puts it back on the default.
    // Windows remembers the choice for the program; we keep it too, to put
    // it back if Windows forgets (a device that was unplugged, for instance).
    public void Route(AudioApp app, string? deviceId)
    {
        ApplyRoute(app, deviceId);
        var device = Devices.FirstOrDefault(d => d.Id == deviceId);
        if (string.IsNullOrEmpty(deviceId) || device == null)
        {
            Settings.Current.Routes.Remove(app.Key);
        }
        else
        {
            Settings.Current.Routes[app.Key] = new Route { DeviceId = deviceId, DeviceName = device.Label };
        }
        Settings.Save();
        app.RoutedDeviceId = deviceId ?? "";
    }

    void ApplyRoute(AudioApp app, string? deviceId)
    {
        var pids = new HashSet<uint>(app.ProcessIds);
        if (!app.IsSystem)
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(app.Key))
                {
                    pids.Add((uint)p.Id);
                    p.Dispose();
                }
            }
            catch
            {
            }
        }
        foreach (uint pid in pids)
        {
            int hr = AudioPolicyConfig.SetForProcess(pid, deviceId);
            if (hr < 0)
            {
                Log.Write($"Route {app.Key} ({pid}) → {deviceId}: 0x{hr:X8}");
            }
        }
        Log.Write($"Routed {app.Key} → {(string.IsNullOrEmpty(deviceId) ? "default" : deviceId)} ({pids.Count} processes)");
    }

    // Saved routes for programs that just started making sound, and for all
    // of them after a device comes back.
    void ApplyRoutes()
    {
        foreach (var app in Apps)
        {
            if (app.IsSystem || !_routeChecked.Add(app.Key))
            {
                continue;
            }
            if (Settings.Current.Routes.TryGetValue(app.Key, out var route) &&
                Devices.Any(d => d.Id == route.DeviceId) &&
                !string.Equals(app.RoutedDeviceId, route.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                ApplyRoute(app, route.DeviceId);
                app.RoutedDeviceId = route.DeviceId;
            }
        }
        // A program that stopped making sound gets checked again next time.
        _routeChecked.IntersectWith(Apps.Select(a => a.Key));
    }

    public string DeviceLabel(string deviceId) =>
        Devices.FirstOrDefault(d => d.Id == deviceId)?.Label ??
        Settings.Current.Routes.Values.FirstOrDefault(r => r.DeviceId == deviceId)?.DeviceName ??
        "Отключённое устройство";

    // --------------------------------------------------------- default device

    public bool SetDefault(AudioDevice device)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClass();
            int hr = policy.SetDefaultEndpoint(device.Id, ERole.Console);
            if (hr >= 0)
            {
                hr = policy.SetDefaultEndpoint(device.Id, ERole.Multimedia);
            }
            if (hr >= 0 && Settings.Current.SwitchCommunications)
            {
                hr = policy.SetDefaultEndpoint(device.Id, ERole.Communications);
            }
            Marshal.ReleaseComObject(policy);
            if (hr < 0)
            {
                Log.Write($"SetDefault {device.Label}: 0x{hr:X8}");
                return false;
            }
            foreach (var d in Devices)
            {
                d.IsDefault = d == device;
            }
            Log.Write($"Default → {device.Label}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("SetDefault failed: " + ex);
            return false;
        }
    }

    // The devices the switch hotkey goes between.
    public List<AudioDevice> QuickDevices()
    {
        var quick = Devices.Where(d => Settings.Current.QuickDevices.Contains(d.Id)).ToList();
        return quick.Count >= 2 ? quick : Devices.ToList();
    }

    // On the first run: headphones and speakers, preferably on the same
    // sound card, instead of going through every virtual device there is.
    public void GuessQuickDevices()
    {
        static bool IsHeadphones(AudioDevice d) => d.Kind is DeviceKind.Headphones or DeviceKind.Headset;
        var headphones = Devices.Where(IsHeadphones).ToList();
        var speakers = Devices.Where(d => d.Kind == DeviceKind.Speakers).ToList();
        if (headphones.Count == 0 || speakers.Count == 0)
        {
            return;
        }
        var first = headphones.FirstOrDefault(d => d.IsDefault) ?? headphones[0];
        var second = speakers.FirstOrDefault(d => d.Adapter == first.Adapter) ?? speakers.FirstOrDefault(d => d.IsDefault) ?? speakers[0];
        Settings.Current.QuickDevices = new List<string> { first.Id, second.Id };
        Log.Write($"Quick devices: {first.Label} ↔ {second.Label}");
    }

    public AudioDevice? SwitchToNext()
    {
        var quick = QuickDevices();
        if (quick.Count < 2)
        {
            return null;
        }
        int current = quick.FindIndex(d => d.IsDefault);
        var next = quick[(current + 1) % quick.Count];
        return SetDefault(next) ? next : null;
    }
}
