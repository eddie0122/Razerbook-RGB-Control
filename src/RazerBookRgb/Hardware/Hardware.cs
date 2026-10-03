using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace RazerBookRgb;

public static class Protocol
{
    public static byte[] Packet(byte commandClass, byte command, params byte[] args)
    {
        if (args.Length > 80) throw new ArgumentOutOfRangeException(nameof(args));
        var p = new byte[91]; // Windows report ID followed by a 90-byte Razer report.
        p[0] = 0; p[2] = 0x3F; p[6] = (byte)args.Length; p[7] = commandClass; p[8] = command;
        Array.Copy(args, 0, p, 9, args.Length);
        for (int i = 3; i <= 88; i++) p[89] ^= p[i];
        return p;
    }
    public static byte[] Static(string hex) { var rgb = Rgb(hex); return Packet(3, 0x0A, 6, rgb[0], rgb[1], rgb[2]); }
    public static byte[] Rgb(string hex)
    {
        if (!Profile.IsColor(hex)) throw new ArgumentException("Use #RRGGBB.");
        return Convert.FromHexString(hex[1..]);
    }
    public static IEnumerable<byte[]> Frames(Profile p)
    {
        p.Validate();
        for (byte row = 0; row < 6; row++)
        {
            var a = new byte[52]; a[0] = 0xFF; a[1] = row; a[2] = 0; a[3] = 15;
            for (int col = 0; col < 16; col++) Array.Copy(Rgb(p.Colors[row * 16 + col]), 0, a, 4 + col * 3, 3);
            yield return Packet(3, 0x0B, a);
        }
        yield return Packet(3, 0x0A, 5, 0);
    }
}

public sealed class BookKeyboard : IDisposable
{
    readonly SafeFileHandle handle;
    readonly int reportLength;
    public string Path { get; }
    BookKeyboard(SafeFileHandle h, string path, int length) { handle = h; Path = path; reportLength = Math.Max(91, length); }

    public static BookKeyboard Open()
    {
        Native.HidD_GetHidGuid(out Guid guid);
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        var errors = new List<string>();
        try
        {
            for (uint i = 0; ; i++)
            {
                var info = new Native.InterfaceData { Size = Marshal.SizeOf<Native.InterfaceData>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref info))
                {
                    if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception();
                    break;
                }
                Native.SetupDiGetDeviceInterfaceDetail(set, ref info, IntPtr.Zero, 0, out uint length, IntPtr.Zero);
                IntPtr detail = Marshal.AllocHGlobal((int)length);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref info, detail, length, out _, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (!path.Contains("vid_1532&pid_026a", StringComparison.OrdinalIgnoreCase) || !path.Contains("mi_02", StringComparison.OrdinalIgnoreCase)) continue;
                    var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (h.IsInvalid) { errors.Add(new Win32Exception().Message); h.Dispose(); continue; }
                    if (Native.HidD_GetPreparsedData(h, out IntPtr pp))
                    {
                        try
                        {
                            if (Native.HidP_GetCaps(pp, out var caps) >= 0 && caps.FeatureReportByteLength >= 91 && caps.UsagePage == 1 && caps.Usage == 2)
                                return new BookKeyboard(h, path, caps.FeatureReportByteLength);
                        }
                        finally { Native.HidD_FreePreparsedData(pp); }
                    }
                    h.Dispose();
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        throw new IOException(errors.Count > 0 ? "Keyboard access failed: " + string.Join("; ", errors) : "Razer Book keyboard not found (1532:026A). Reconnect or restart Windows, then retry.");
    }

    public byte[] Exchange(byte[] packet)
    {
        var request = new byte[reportLength]; Array.Copy(packet, request, packet.Length);
        if (!Native.HidD_SetFeature(handle, request, request.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot send lighting command. Close other RGB apps and retry.");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Thread.Sleep(12);
            var response = new byte[reportLength];
            if (!Native.HidD_GetFeature(handle, response, response.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read keyboard response.");
            if (response[1] == 1) continue;
            if (response[7] != packet[7] || response[8] != packet[8] || response[2] != packet[2]) throw new IOException("Keyboard response did not match. Close Synapse / other RGB controllers and retry.");
            byte crc = 0; for (int i = 3; i <= 88; i++) crc ^= response[i];
            if (crc != response[89]) throw new IOException("Keyboard response checksum failed.");
            if (response[1] != 2) throw new IOException("Keyboard rejected command " + packet[7].ToString("X2") + ":" + packet[8].ToString("X2") + " (status " + response[1] + ").");
            return response;
        }
        throw new IOException("Keyboard stayed busy. Close other RGB controllers and retry.");
    }
    public string Probe()
    {
        var r = Exchange(Protocol.Packet(0, 0x81, 0, 0));
        return "Razer Book 13 connected · firmware " + r[9] + "." + r[10];
    }
    // A read-only device-mode request keeps the Book's lighting watchdog alive.
    // Do not switch to driver mode: that would alter Fn-key handling.
    public void KeepAlive() => Exchange(Protocol.Packet(0, 0x84, 0, 0));
    public void SetBrightness(byte value) => Exchange(Protocol.Packet(0x0E, 4, 1, value));
    public byte ReadBrightness() => Exchange(Protocol.Packet(0x0E, 0x84, 1, 0))[10];
    public void Apply(Profile p)
    {
        p.Validate();
        // Only lighting commands: never modify firmware, fans, performance, or key bindings.
        SetBrightness((byte)Math.Round(p.Brightness * 255.0 / 100));
        if (p.Mode == "Static") Exchange(Protocol.Static(p.StaticColor));
        else foreach (var frame in Protocol.Frames(p)) Exchange(frame);
    }
    public void Dispose() => handle.Dispose();
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
    [StructLayout(LayoutKind.Sequential)] internal struct Caps
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices, NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices, NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_SetFeature(SafeFileHandle h, byte[] data, int length);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] internal static extern bool HidD_GetFeature(SafeFileHandle h, byte[] data, int length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string? enumerator, IntPtr window, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
    [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
