using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace RazerBookRgb;

// Only the timestamp of a keyboard make event is retained. No characters, key
// codes, or typed text are stored. Mouse activity is deliberately not registered.
public sealed class KeyboardActivity : IDisposable
{
    readonly HwndSource source;
    public long LastKeyAt { get; private set; } = Environment.TickCount64;
    public KeyboardActivity(IntPtr window)
    {
        source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("Window handle unavailable.");
        var devices = new[] { new RawDevice { Page = 1, Usage = 6, Flags = 0x100, Target = window } };
        if (!RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RawDevice>())) throw new Win32Exception();
        source.AddHook(OnMessage);
    }
    public void Reset() => LastKeyAt = Environment.TickCount64;
    IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0xFF) return IntPtr.Zero;
        uint headerSize = (uint)(8 + 2 * IntPtr.Size), size = 0;
        if (GetRawInputData(lParam, 0x10000003, IntPtr.Zero, ref size, headerSize) != 0 || size < headerSize + 16 || size > 4096) return IntPtr.Zero;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, 0x10000003, buffer, ref size, headerSize) == size && Marshal.ReadInt32(buffer) == 1)
            {
                ushort flags = (ushort)Marshal.ReadInt16(buffer, (int)headerSize + 2);
                if ((flags & 1) == 0) Reset(); // Key down / repeat, not release.
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        // Leave unhandled so WPF calls DefWindowProc for foreground cleanup.
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        source.RemoveHook(OnMessage);
        RegisterRawInputDevices(new[] { new RawDevice { Page = 1, Usage = 6, Flags = 1 } }, 1, (uint)Marshal.SizeOf<RawDevice>());
    }
    [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort Page, Usage; public uint Flags; public IntPtr Target; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
}

public enum LightingAction { None, KeepAlive, TurnOff, Restore }

// Pure timing policy, kept separate from USB and UI so boundary/wake behavior is testable.
public sealed class IdleLightingPolicy
{
    public bool IsDark { get; private set; }
    long lastHeartbeat;
    public void Applied(long now) { IsDark = false; lastHeartbeat = now; }
    public LightingAction Next(long now, long lastKeyAt, int timeoutSeconds, bool locked)
    {
        bool dark = locked || (timeoutSeconds > 0 && now - lastKeyAt >= (long)timeoutSeconds * 1000);
        if (dark) return IsDark ? LightingAction.None : LightingAction.TurnOff;
        if (IsDark) return LightingAction.Restore;
        return now - lastHeartbeat >= 1500 ? LightingAction.KeepAlive : LightingAction.None;
    }
    public void Completed(LightingAction action, long now)
    {
        if (action == LightingAction.TurnOff) IsDark = true;
        if (action == LightingAction.Restore) IsDark = false;
        lastHeartbeat = now;
    }
}
