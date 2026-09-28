using System.Runtime.InteropServices;

namespace Baton.Media;

/// <summary>
/// Per-app volume, the same slider the Windows volume mixer shows. An app is matched by process
/// name, so a browser's audio (played by one of its child processes) counts as the browser's.
/// </summary>
public static class AppVolume
{
    /// <summary>The app's volume (0–1) on the default speakers, or null when it has no audio session.</summary>
    public static double? Get(string processName)
    {
        double? volume = null;
        Visit(processName, control => volume = Math.Max(volume ?? 0, control.GetMasterVolume(out var level) == 0 ? level : 0));
        return volume;
    }

    /// <summary>Sets every audio session of the app; false when it has none.</summary>
    public static bool Set(string processName, double volume)
    {
        var level = (float)Math.Clamp(volume, 0, 1);
        var found = false;
        Visit(processName, control =>
        {
            var context = Guid.Empty;
            control.SetMasterVolume(level, ref context);
            if (level > 0)
            {
                control.SetMute(false, ref context);
            }

            found = true;
        });
        return found;
    }

    private static void Visit(string processName, Action<ISimpleAudioVolume> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0)
            {
                return;
            }

            var managerId = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref managerId, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var managerObject) != 0)
            {
                return;
            }

            var manager = (IAudioSessionManager2)managerObject;
            if (manager.GetSessionEnumerator(out var sessions) != 0)
            {
                return;
            }

            sessions.GetCount(out var count);
            for (var index = 0; index < count; index++)
            {
                if (sessions.GetSession(index, out var session) != 0 || session is not IAudioSessionControl2 control)
                {
                    continue;
                }

                control.GetProcessId(out var processId);
                if (processId != 0 && string.Equals(NameOf((int)processId), processName, StringComparison.OrdinalIgnoreCase))
                {
                    action((ISimpleAudioVolume)control);
                }
            }
        }
        catch (COMException)
        {
            // No audio device, or the audio service restarted: no volume to report.
        }
        finally
        {
            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static string? NameOf(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, IntPtr context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, IntPtr context);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(IntPtr grouping, IntPtr context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
