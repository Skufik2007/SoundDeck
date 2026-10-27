using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace SoundDeck;

internal sealed class Route
{
    public string DeviceId { get; set; } = "";
    // For showing the rule while the device is unplugged.
    public string DeviceName { get; set; } = "";
}

internal sealed class HotkeySettings
{
    public string SwitchOutput { get; set; } = "Ctrl+Alt+S";
    public string AppVolumeUp { get; set; } = "Ctrl+Alt+Up";
    public string AppVolumeDown { get; set; } = "Ctrl+Alt+Down";
    public string AppMute { get; set; } = "Ctrl+Alt+M";
    public string OpenPanel { get; set; } = "Ctrl+Alt+D";
}

internal sealed class Settings
{
    // Program (exe name without extension, lower case) → its output device.
    public Dictionary<string, Route> Routes { get; set; } = new();

    // Devices the switch hotkey goes between, e.g. headphones and speakers.
    // With fewer than two, it goes through all of them.
    public List<string> QuickDevices { get; set; } = new();
    public bool QuickDevicesGuessed { get; set; }

    // Also make the switched-to device the default for calls (Discord,
    // Teams and others that use the "communications" device).
    public bool SwitchCommunications { get; set; } = true;

    public int VolumeStepPercent { get; set; } = 5;

    public HotkeySettings Hotkeys { get; set; } = new();

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoundDeck");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Current { get; private set; } = new();

    // Readable Cyrillic and "+" in hotkeys: the file is meant for editing.
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception ex)
        {
            Log.Write("Settings load failed: " + ex.Message);
            Current = new Settings();
        }
        Current.Hotkeys ??= new HotkeySettings();
        Current.Routes ??= new();
        Current.QuickDevices ??= new();
        Save();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Write("Settings save failed: " + ex.Message);
        }
    }
}

internal static class Log
{
    static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
                string file = Path.Combine(Settings.Folder, "log.txt");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                {
                    File.Delete(file);
                }
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
