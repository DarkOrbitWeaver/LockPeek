using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LockPeek.Windows;

public static class WindowActivation
{
    public static void GrantToPipeServer(SafePipeHandle pipe)
    {
        if (GetNamedPipeServerProcessId(pipe, out uint pid)) AllowSetForegroundWindow(pid);
    }

    public static bool RestoreAndActivate(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, 9); // SW_RESTORE preserves a previously maximized window.
        return SetForegroundWindow(window);
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
