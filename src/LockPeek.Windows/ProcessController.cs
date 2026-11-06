using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using LockPeek.Core;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace LockPeek.Windows;

public sealed class ProcessController : IProcessController
{
    private static readonly string Owner = WindowsIdentity.GetCurrent().User!.Value;
    private static readonly int Session = Process.GetCurrentProcess().SessionId;
    private static readonly object ServiceGate = new();
    private static string[]? serviceRoots;
    private static long serviceRootsUpdated;

    private static string[] InspectionServiceRoots()
    {
        lock (ServiceGate)
        {
            long now = Stopwatch.GetTimestamp();
            if (serviceRoots is null || Stopwatch.GetElapsedTime(serviceRootsUpdated, now) > TimeSpan.FromSeconds(2))
            {
                serviceRoots = ServiceRoots().ToArray();
                serviceRootsUpdated = now;
            }
            return serviceRoots;
        }
    }

    public ProcessTarget Inspect(DetectedProcess process)
    {
        using var handle = OpenProcess(0x1000 | 0x100000, false, process.Identity.Pid);
        var evidence = InspectHandle(process, handle, useCache: true);
        return new(process, evidence, SafetyPolicy.Classify(evidence));
    }

    private static ProcessEvidence InspectHandle(DetectedProcess detection, SafeProcessHandle handle, bool useCache = false)
    {
        var id = detection.Identity;

        ProcessEvidence Unknown(string message) => new(
            id,
            detection.Name,
            detection.Name,
            Verified: false,
            SameUser: false,
            SameSession: false,
            Elevated: true,
            Critical: true,
            Protected: true,
            Service: true,
            WindowsComponent: true,
            SecurityOrHost: true,
            HasSafeWindow: false,
            Error: message);

        try
        {
            if (handle.IsInvalid)
            {
                return Unknown("Process exited or access was denied");
            }

            if (!GetProcessTimes(handle, out long created, out _, out _, out _) || created != id.CreationTime)
            {
                return Unknown("Process identity changed or could not be verified");
            }

            if (WaitForSingleObject(handle, 0) != 258)
            {
                return Unknown("Process has exited");
            }

            var path = new StringBuilder(32768);
            int length = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref length))
            {
                return Unknown("Executable could not be verified");
            }

            if (!OpenProcessToken(handle, 8, out var token))
            {
                return Unknown("Process ownership could not be verified");
            }

            bool sameUser;
            bool elevated;
            string processOwner;

            using (token)
            {
                GetTokenInformation(token, 1, IntPtr.Zero, 0, out int required);
                var data = Marshal.AllocHGlobal(required);
                try
                {
                    if (!GetTokenInformation(token, 1, data, required, out _))
                    {
                        return Unknown("Process owner is unavailable");
                    }
                    processOwner = new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value;
                    sameUser = processOwner == Owner;

                    if (!GetTokenInformation(token, 20, data, 4, out _))
                    {
                        return Unknown("Elevation could not be verified: " + new Win32Exception().Message);
                    }
                    elevated = Marshal.ReadInt32(data) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(data);
                }
            }

            if (!ProcessIdToSessionId(id.Pid, out int session) || !IsProcessCritical(handle, out bool critical))
            {
                return Unknown("Session or critical status could not be verified");
            }

            uint protection = 0;
            if (!GetProcessInformation(handle, 7, ref protection, 4))
            {
                return Unknown("Process protection could not be verified");
            }

            string exe = path.ToString();
            bool windows = IsWithin(exe, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            bool serviceProduct = (useCache ? InspectionServiceRoots() : ServiceRoots()).Any(root => IsWithin(exe, root));

            string display = detection.Name;
            try
            {
                display = FileVersionInfo.GetVersionInfo(exe).FileDescription ?? display;
            }
            catch (Exception)
            {
            }

            return new(
                id,
                exe,
                display,
                Verified: true,
                SameUser: sameUser,
                SameSession: session == Session,
                Elevated: elevated,
                Critical: critical,
                Protected: protection != 0xFFFFFFFE,
                Service: detection.Service.Length > 0 || detection.ApplicationType == 3 || session == 0,
                WindowsComponent: windows,
                SecurityOrHost: serviceProduct || id.Pid == Environment.ProcessId,
                HasSafeWindow: SafeWindows(id.Pid).Count > 0,
                Owner: sameUser ? WindowsIdentity.GetCurrent().Name : processOwner,
                SessionId: session);
        }
        catch (Exception e) when (e is Win32Exception or ArgumentException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return Unknown(e.Message);
        }
    }

    private static bool IsWithin(string path, string root) =>
        root.Length > 0 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> ServiceRoots()
    {
        RegistryKey? services = null;
        try
        {
            services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        }
        catch (Exception)
        {
        }

        if (services is null) return [];

        using (services)
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] subKeyNames;
            try
            {
                subKeyNames = services.GetSubKeyNames();
            }
            catch (Exception)
            {
                return [];
            }

            foreach (var name in subKeyNames)
            {
                try
                {
                    using var entry = services.OpenSubKey(name);
                    string? image = entry?.GetValue("ImagePath") as string;
                    if (string.IsNullOrWhiteSpace(image)) continue;
                    image = Environment.ExpandEnvironmentVariables(image).Trim();
                    string exe = image.StartsWith('"')
                        ? image.Split('"').ElementAtOrDefault(1) ?? ""
                        : image.Contains(".exe", StringComparison.OrdinalIgnoreCase)
                            ? image[..(image.IndexOf(".exe", StringComparison.OrdinalIgnoreCase) + 4)]
                            : image;
                    if (exe.StartsWith(@"\??\")) exe = exe[4..];
                    if (Path.IsPathFullyQualified(exe) && Path.GetDirectoryName(exe) is { Length: > 3 } dir)
                    {
                        roots.Add(dir);
                    }
                }
                catch (Exception)
                {
                    // Skip inaccessible service keys without failing the entire scan
                }
            }
            return roots;
        }
    }

    public Task<ActionOutcome> ActAsync(ProcessTarget target, ProcessAction action, CancellationToken cancellation) => Task.Run(async () =>
    {
        cancellation.ThrowIfCancellationRequested();

        using var handle = OpenProcess(
            0x1000 | 0x100000 | (action == ProcessAction.ForceEnd ? 1u : 0u),
            false,
            target.Detection.Identity.Pid);

        var fresh = InspectHandle(target.Detection, handle);
        var eligibility = SafetyPolicy.Classify(fresh);
        bool allowed = action == ProcessAction.ForceEnd ? eligibility.CanForceEnd : eligibility.CanClose;

        if (!allowed)
        {
            return new ActionOutcome(target.Detection.Identity, target.Detection.Name, "Skipped: " + eligibility.Reason, false);
        }

        if (action == ProcessAction.ForceEnd)
        {
            if (!TerminateProcess(handle, 1))
            {
                return new(target.Detection.Identity, target.Detection.Name, "Force end failed: " + new Win32Exception().Message, false);
            }
        }
        else
        {
            bool posted = false;
            foreach (var window in SafeWindows(fresh.Identity.Pid))
            {
                GetWindowThreadProcessId(window, out int pid);
                if (pid != fresh.Identity.Pid || WaitForSingleObject(handle, 0) != 258) continue;
                posted |= PostMessage(window, 0x10, IntPtr.Zero, IntPtr.Zero);
            }
            if (!posted)
            {
                return new(target.Detection.Identity, target.Detection.Name, "No close request could be sent", false);
            }
        }

        var wait = Stopwatch.StartNew();
        while (WaitForSingleObject(handle, 0) == 258 && wait.Elapsed < TimeSpan.FromSeconds(3.5))
        {
            await Task.Delay(80);
        }

        bool exited = WaitForSingleObject(handle, 0) == 0;
        string message = exited
            ? "Exited"
            : action == ProcessAction.CloseNormally
                ? "Close requested · still running; check for a Save dialog"
                : "Force end requested · exit not yet observed";

        return new(target.Detection.Identity, target.Detection.Name, message, true, exited);
    }, cancellation);

    private static List<IntPtr> SafeWindows(int pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out int owner);
            if (owner != pid || !IsWindowVisible(window))
                return true;

            long exStyle = GetWindowLongPtr(window, -20).ToInt64();
            if ((exStyle & 0x80) != 0) // WS_EX_TOOLWINDOW
                return true;

            IntPtr ownerHwnd = GetWindow(window, 4);
            if (ownerHwnd == IntPtr.Zero)
            {
                result.Add(window);
            }
            else
            {
                GetWindowThreadProcessId(ownerHwnd, out int parentPid);
                if (parentPid == pid)
                {
                    result.Add(window);
                }
            }

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr state);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, IntPtr data, int size, out int required);

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(int pid, out int session);

    [DllImport("kernel32.dll")]
    private static extern bool IsProcessCritical(SafeProcessHandle process, out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(SafeProcessHandle process, int type, ref uint data, uint size);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint timeout);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr state);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
}
