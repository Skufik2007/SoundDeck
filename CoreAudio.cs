using System;
using System.Runtime.InteropServices;

namespace SoundDeck.Interop;

// Core Audio (MMDevice API and WASAPI sessions), declared by hand. Every
// method returns its HRESULT so a device that vanishes mid-call doesn't throw.

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

internal static class DeviceState
{
    public const uint Active = 0x1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;

    public PropertyKey(string formatId, int propertyId)
    {
        FormatId = new Guid(formatId);
        PropertyId = propertyId;
    }

    public static readonly PropertyKey DeviceFriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static readonly PropertyKey DeviceDescription = new("a45c254e-df1c-4efd-8020-67d146a850e0", 2);
    public static readonly PropertyKey InterfaceFriendlyName = new("026e516e-b814-414b-83cd-856d6fef4822", 2);
    public static readonly PropertyKey EndpointFormFactor = new("1da5d803-d492-4edd-8c23-e0c0ffee7f0e", 0);
}

// PROPVARIANT is 24 bytes on x64; only strings and 32-bit integers are read.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public IntPtr Pointer;
    [FieldOffset(8)] public uint UInt;

    const ushort VT_I4 = 3;
    const ushort VT_UI4 = 19;
    const ushort VT_LPWSTR = 31;

    public string? AsString() => Type == VT_LPWSTR ? Marshal.PtrToStringUni(Pointer) : null;

    public uint? AsUInt() => Type is VT_UI4 or VT_I4 ? UInt : null;

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant pvar);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorClass
{
}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out uint state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
    [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
}

internal enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2,
}

[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out AudioSessionState state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
    [PreserveSig] int GetGroupingParam(out Guid grouping);
    [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid context);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint processId);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig] int SetMasterVolume(float level, ref Guid context);
    [PreserveSig] int GetMasterVolume(out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

// The system default device. Undocumented, but unchanged since Windows 7 and
// used by every "switch audio device" tool.
[ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
internal class PolicyConfigClass
{
}

[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPolicyConfig
{
    [PreserveSig] int GetMixFormat(IntPtr deviceId, IntPtr format);
    [PreserveSig] int GetDeviceFormat(IntPtr deviceId, int isDefault, IntPtr format);
    [PreserveSig] int ResetDeviceFormat(IntPtr deviceId);
    [PreserveSig] int SetDeviceFormat(IntPtr deviceId, IntPtr endpointFormat, IntPtr mixFormat);
    [PreserveSig] int GetProcessingPeriod(IntPtr deviceId, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
    [PreserveSig] int SetProcessingPeriod(IntPtr deviceId, IntPtr period);
    [PreserveSig] int GetShareMode(IntPtr deviceId, IntPtr mode);
    [PreserveSig] int SetShareMode(IntPtr deviceId, IntPtr mode);
    [PreserveSig] int GetPropertyValue(IntPtr deviceId, int fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetPropertyValue(IntPtr deviceId, int fxStore, IntPtr key, IntPtr value);
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
}

internal static class AudioIids
{
    public static Guid EndpointVolume = typeof(IAudioEndpointVolume).GUID;
    public static Guid MeterInformation = typeof(IAudioMeterInformation).GUID;
    public static Guid SessionManager2 = typeof(IAudioSessionManager2).GUID;
    public const uint ClsCtxAll = 0x17;
}

// The per-app output device, as in Settings → Sound → Volume mixer. Windows
// keeps it per program and applies it to the program's streams right away.
// It's the WinRT factory of Windows.Media.Internal.AudioPolicyConfig, called
// through its vtable since .NET 5+ has no built-in IInspectable interop. The
// layout is the one EarTrumpet uses: the methods we need come after the 6
// IInspectable slots and 19 others.
internal static unsafe class AudioPolicyConfig
{
    const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    static readonly Guid IidModern = new("ab3d4648-e242-459f-b02f-541c70306324"); // 21H2 and later
    static readonly Guid IidDownlevel = new("2a59116d-6c4f-45e0-a74f-707e3fef9258");

    const int SlotSetPersisted = 25;
    const int SlotGetPersisted = 26;
    const int SlotClearAll = 27;

    const string RenderInterface = "{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const string Prefix = @"\\?\SWD#MMDEVAPI#";

    static IntPtr _factory;
    static bool _tried;

    public static string? LoadError { get; private set; }

    static IntPtr Factory
    {
        get
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    Guid iid = Environment.OSVersion.Version.Build >= 21390 ? IidModern : IidDownlevel;
                    int hr = WinRt.WindowsCreateString(ClassName, ClassName.Length, out IntPtr name);
                    Marshal.ThrowExceptionForHR(hr);
                    try
                    {
                        hr = WinRt.RoGetActivationFactory(name, ref iid, out _factory);
                    }
                    finally
                    {
                        WinRt.WindowsDeleteString(name);
                    }
                    if (hr < 0)
                    {
                        LoadError = $"AudioPolicyConfig недоступен (0x{hr:X8})";
                        _factory = IntPtr.Zero;
                    }
                }
                catch (Exception ex)
                {
                    LoadError = ex.Message;
                }
            }
            return _factory;
        }
    }

    public static bool IsAvailable => Factory != IntPtr.Zero;

    static IntPtr Slot(int index) => (*(IntPtr**)Factory)[index];

    // deviceId null or empty means "follow the system default".
    public static int SetForProcess(uint processId, string? deviceId)
    {
        if (!IsAvailable)
        {
            return unchecked((int)0x80004001);
        }
        IntPtr hstring = IntPtr.Zero;
        if (!string.IsNullOrEmpty(deviceId))
        {
            string full = Prefix + deviceId + "#" + RenderInterface;
            int hr = WinRt.WindowsCreateString(full, full.Length, out hstring);
            if (hr < 0)
            {
                return hr;
            }
        }
        try
        {
            var set = (delegate* unmanaged[Stdcall]<IntPtr, uint, EDataFlow, ERole, IntPtr, int>)Slot(SlotSetPersisted);
            int hr = set(Factory, processId, EDataFlow.Render, ERole.Multimedia, hstring);
            if (hr >= 0)
            {
                hr = set(Factory, processId, EDataFlow.Render, ERole.Console, hstring);
            }
            return hr;
        }
        finally
        {
            if (hstring != IntPtr.Zero)
            {
                WinRt.WindowsDeleteString(hstring);
            }
        }
    }

    // The MMDevice id the program is pinned to, or "" when it follows the default.
    public static string GetForProcess(uint processId)
    {
        if (!IsAvailable)
        {
            return "";
        }
        var get = (delegate* unmanaged[Stdcall]<IntPtr, uint, EDataFlow, ERole, IntPtr*, int>)Slot(SlotGetPersisted);
        IntPtr hstring = IntPtr.Zero;
        int hr = get(Factory, processId, EDataFlow.Render, ERole.Multimedia, &hstring);
        if (hr < 0 || hstring == IntPtr.Zero)
        {
            return "";
        }
        try
        {
            char* buffer = WinRt.WindowsGetStringRawBuffer(hstring, out uint length);
            string full = new(buffer, 0, (int)length);
            return ParseDeviceId(full);
        }
        finally
        {
            WinRt.WindowsDeleteString(hstring);
        }
    }

    public static int ClearAll()
    {
        if (!IsAvailable)
        {
            return unchecked((int)0x80004001);
        }
        var clear = (delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(SlotClearAll);
        return clear(Factory);
    }

    // \\?\SWD#MMDEVAPI#{0.0.0.00000000}.{guid}#{interface} → {0.0.0.00000000}.{guid}
    public static string ParseDeviceId(string full)
    {
        int start = full.IndexOf("MMDEVAPI#", StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return full;
        }
        start += "MMDEVAPI#".Length;
        int end = full.IndexOf('#', start);
        return end < 0 ? full[start..] : full[start..end];
    }
}

internal static unsafe class WinRt
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    public static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern char* WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
