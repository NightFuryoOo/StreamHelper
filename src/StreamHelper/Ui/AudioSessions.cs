using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

internal static class AudioSessions
{
    public static readonly Guid Group = new("5B0E6C1A-2F7D-4C55-9E3B-8A41D6F0C2E7");

    private static bool _failed;

    public static void GroupMine(string name)
    {
        try
        {
            var context = Guid.Empty;
            foreach (var session in Mine())
            {
                if (session.GetGroupingParam(out var current) == 0 && current == Group && session.GetDisplayName(out var shown) == 0 && shown == name) continue;
                var group = Group;
                session.SetGroupingParam(ref group, ref context);
                session.SetDisplayName(name, ref context);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            if (_failed) return;
            _failed = true;
            Log.Write("Grouping the sounds in the volume mixer failed: " + ex.Message);
        }
    }

    private static IEnumerable<IAudioSessionControl2> Mine()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) yield break;
        var iid = typeof(IAudioSessionManager2).GUID;
        if (device.Activate(ref iid, 23, IntPtr.Zero, out var manager) != 0) yield break;
        if (((IAudioSessionManager2)manager).GetSessionEnumerator(out var sessions) != 0) yield break;
        sessions.GetCount(out var count);
        var me = (uint)Environment.ProcessId;
        for (var i = 0; i < count; i++)
        {
            if (sessions.GetSession(i, out var control) != 0 || control is not IAudioSessionControl2 session) continue;
            if (session.GetProcessId(out var owner) >= 0 && owner == me) yield return session;
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr volume);
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
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
    }
}
