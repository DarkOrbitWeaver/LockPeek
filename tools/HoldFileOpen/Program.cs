using System.Runtime.InteropServices;

var held = args.Where(p => !p.StartsWith("--")).Select(p => new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)).ToArray();
Native.Run(args.Contains("--ignore-close"));
foreach (var stream in held) stream.Dispose();
internal static class Native
{
    private static readonly WndProc Callback = WindowProc;
    private static bool ignore;
    internal static void Run(bool ignoreClose)
    {
        ignore = ignoreClose;
        var wc = new WNDCLASS { proc = Marshal.GetFunctionPointerForDelegate(Callback), name = "LockPeekDisposableHolder", instance = GetModuleHandle(null) };
        if (RegisterClass(ref wc) == 0) throw new System.ComponentModel.Win32Exception();
        var hwnd = CreateWindowEx(0, wc.name, "LockPeek disposable test app", 0x10CF0000, 40, 40, 440, 180, IntPtr.Zero, IntPtr.Zero, wc.instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        Console.WriteLine("READY"); Console.Out.Flush();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
    }
    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == 0x10 && ignore) return IntPtr.Zero;
        if (msg == 2) { PostQuitMessage(0); return IntPtr.Zero; }
        return DefWindowProc(hwnd, msg, wp, lp);
    }
    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WNDCLASS { public uint style; public IntPtr proc; public int clsExtra, wndExtra; public IntPtr instance, icon, cursor, background; public string? menu; public string name; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wp, lp; public uint time; public int x, y; public uint privateValue; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WNDCLASS wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
