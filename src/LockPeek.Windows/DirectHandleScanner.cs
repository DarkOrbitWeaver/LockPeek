using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LockPeek.Core;

namespace LockPeek.Windows;

public sealed class DirectHandleScanner : IResourceScanner
{
    private const int SystemExtendedHandleInformation = 64;
    private const int StatusInfoLengthMismatch = -1073741820; // 0xC0000004
    private const uint ProcessDupHandle = 0x0040;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint DuplicateSameAccess = 0x0002;
    private const uint FileTypeDisk = 0x0001;

    private static volatile int cachedFileObjectTypeIndex = -1;
    private static readonly object probeLock = new();

    private readonly RestartManager fallbackScanner = new();

    public async Task<ScanResult> ScanAsync(IEnumerable<string> paths, IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default)
    {
        var deduplicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (string.IsNullOrWhiteSpace(p))
            {
                continue;
            }

            try
            {
                deduplicated.Add(Path.GetFullPath(p).TrimEnd('\\'));
            }
            catch
            {
                deduplicated.Add(p.TrimEnd('\\'));
            }
        }

        var inputList = deduplicated.ToArray();
        if (inputList.Length == 0)
        {
            return new ScanResult([], [], 0, 0, false, TimeSpan.Zero);
        }

        try
        {
            return await Task.Run(() => ScanCore(inputList, progress, cancellation), cancellation);
        }
        catch (OperationCanceledException)
        {
            return new ScanResult([], [], inputList.Length, 0, true, TimeSpan.Zero);
        }
        catch
        {
            // fallback to restart manager if direct handle scan fails
            return await fallbackScanner.ScanAsync(inputList, progress, cancellation);
        }
    }

    private ScanResult ScanCore(string[] inputList, IProgress<ScanProgress>? progress, CancellationToken cancellation)
    {
        var watch = Stopwatch.StartNew();
        int fileObjectType = GetOrProbeFileObjectTypeIndex();

        var fileTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirTargets = new List<string>();
        var issues = new List<ScanIssue>();
        int missingTargets = 0;

        foreach (var raw in inputList)
        {
            cancellation.ThrowIfCancellationRequested();
            string normalized = Path.GetFullPath(raw).TrimEnd('\\');

            if (Directory.Exists(normalized))
            {
                dirTargets.Add(normalized);
            }
            else if (File.Exists(normalized))
            {
                fileTargets.Add(normalized);
            }
            else
            {
                // target doesn't exist anymore on disk
                missingTargets++;
                issues.Add(new ScanIssue(normalized, "Item does not exist or was deleted."));
            }
        }

        var pidsWithHandles = QuerySystemFileHandles(fileObjectType, cancellation);
        var foundProcesses = new ConcurrentDictionary<ProcessIdentity, DetectedProcess>();
        IntPtr currentProcessHandle = GetCurrentProcess();

        int totalProcesses = pidsWithHandles.Count;
        int processedProcesses = 0;
        int inaccessibleProcesses = 0;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount)),
            CancellationToken = cancellation
        };

        Parallel.ForEach(pidsWithHandles, parallelOptions, kvp =>
        {
            int pid = kvp.Key;
            var handles = kvp.Value;
            var pathBuilder = new StringBuilder(1024);

            int done = Interlocked.Increment(ref processedProcesses);
            progress?.Report(new ScanProgress(done, totalProcesses));

            IntPtr processHandle = OpenProcess(ProcessDupHandle | ProcessQueryLimitedInformation, false, pid);
            if (processHandle == IntPtr.Zero)
            {
                // system or protected process we can't open
                Interlocked.Increment(ref inaccessibleProcesses);
                return;
            }

            try
            {
                if (!GetProcessTimes(processHandle, out long creationTime, out _, out _, out _))
                {
                    return;
                }

                foreach (var sourceHandle in handles)
                {
                    if (cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    if (!DuplicateHandle(processHandle, sourceHandle, currentProcessHandle, out IntPtr duplicatedHandle, 0, false, DuplicateSameAccess))
                    {
                        continue;
                    }

                    try
                    {
                        // Exclude non-disk handles (named pipes, sockets, character devices) to avoid synchronous hangs
                        if (GetFileType(duplicatedHandle) != FileTypeDisk)
                        {
                            continue;
                        }

                        pathBuilder.Clear();
                        uint pathLen = GetFinalPathNameByHandleW(duplicatedHandle, pathBuilder, (uint)pathBuilder.Capacity, 0);
                        if (pathLen >= (uint)pathBuilder.Capacity && pathLen < 32768)
                        {
                            pathBuilder.EnsureCapacity((int)pathLen + 1);
                            pathLen = GetFinalPathNameByHandleW(duplicatedHandle, pathBuilder, (uint)pathBuilder.Capacity, 0);
                        }

                        if (pathLen > 0)
                        {
                            string resolved = NormalizeResolvedPath(pathBuilder.ToString());
                            if (MatchesAnyTarget(resolved, fileTargets, dirTargets))
                            {
                                var identity = new ProcessIdentity(pid, creationTime);
                                if (!foundProcesses.ContainsKey(identity))
                                {
                                    string exePath = QueryProcessPath(processHandle);
                                    string displayName = ResolveDisplayName(pid, exePath);
                                    int appType = exePath.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) ? 4 : 0;

                                    foundProcesses[identity] = new DetectedProcess(identity, displayName, "", appType);
                                }
                                break;
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(duplicatedHandle);
                    }
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }
        });

        watch.Stop();

        if (inaccessibleProcesses > 0)
        {
            issues.Add(new ScanIssue("system", $"{inaccessibleProcesses} system processes could not be inspected due to permissions.", true));
        }

        return new ScanResult(
            foundProcesses.Values.ToArray(),
            issues.ToArray(),
            inputList.Length,
            inputList.Length - missingTargets,
            cancellation.IsCancellationRequested,
            watch.Elapsed);
    }

    private static bool MatchesAnyTarget(string resolved, HashSet<string> fileTargets, List<string> dirTargets)
    {
        if (fileTargets.Contains(resolved))
        {
            return true;
        }

        foreach (var dir in dirTargets)
        {
            if (resolved.Equals(dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (resolved.Length > dir.Length &&
                resolved[dir.Length] == '\\' &&
                resolved.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeResolvedPath(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..].TrimEnd('\\');
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            return path[4..].TrimEnd('\\');
        }

        return path.TrimEnd('\\');
    }

    private static string QueryProcessPath(IntPtr processHandle)
    {
        var sb = new StringBuilder(1024);
        int size = sb.Capacity;
        if (QueryFullProcessImageName(processHandle, 0, sb, ref size))
        {
            return sb.ToString();
        }
        return "";
    }

    private static string ResolveDisplayName(int pid, string exePath)
    {
        if (!string.IsNullOrEmpty(exePath))
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    return info.FileDescription;
                }
            }
            catch
            {
            }

            string name = Path.GetFileNameWithoutExtension(exePath);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.ProcessName;
        }
        catch
        {
            return $"Process {pid}";
        }
    }

    private static Dictionary<int, List<IntPtr>> QuerySystemFileHandles(int fileObjectType, CancellationToken cancellation)
    {
        var result = new Dictionary<int, List<IntPtr>>();
        int bufferSize = 8 * 1024 * 1024; // 8 MB initial buffer
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);

        try
        {
            int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, out int returnLength);
            int retries = 0;
            while (status == StatusInfoLengthMismatch && retries++ < 8)
            {
                cancellation.ThrowIfCancellationRequested();
                int nextBufferSize = Math.Max(returnLength + (1024 * 1024), bufferSize * 2);
                IntPtr newBuffer = Marshal.AllocHGlobal(nextBufferSize);
                Marshal.FreeHGlobal(buffer);
                buffer = newBuffer;
                bufferSize = nextBufferSize;
                status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, out returnLength);
            }

            if (status != 0)
            {
                throw new InvalidOperationException($"NtQuerySystemInformation failed with NTSTATUS 0x{status:X8}");
            }

            long handleCount = Marshal.ReadInt64(buffer);
            IntPtr entryPtr = buffer + 16;
            int currentPid = Environment.ProcessId;

            for (long i = 0; i < handleCount; i++)
            {
                if ((i & 0x7FFF) == 0)
                {
                    cancellation.ThrowIfCancellationRequested();
                }

                ushort objType = (ushort)Marshal.ReadInt16(entryPtr + 30);
                if (fileObjectType <= 0 || objType == fileObjectType)
                {
                    int pid = (int)Marshal.ReadInt64(entryPtr + 8);
                    IntPtr handleValue = (IntPtr)Marshal.ReadInt64(entryPtr + 16);

                    if (pid != currentPid && pid > 4)
                    {
                        if (!result.TryGetValue(pid, out var list))
                        {
                            list = new List<IntPtr>();
                            result[pid] = list;
                        }
                        list.Add(handleValue);
                    }
                }

                entryPtr += 40;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private static int GetOrProbeFileObjectTypeIndex()
    {
        if (cachedFileObjectTypeIndex >= 0)
        {
            return cachedFileObjectTypeIndex;
        }

        lock (probeLock)
        {
            if (cachedFileObjectTypeIndex >= 0)
            {
                return cachedFileObjectTypeIndex;
            }

            string probePath = Path.Combine(Path.GetTempPath(), $"LockPeekProbe_{Guid.NewGuid():N}.tmp");
            try
            {
                using var fs = new FileStream(probePath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                IntPtr handle = fs.SafeFileHandle.DangerousGetHandle();
                int myPid = Environment.ProcessId;

                int bufferSize = 4 * 1024 * 1024;
                IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
                try
                {
                    int status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, out int returnLength);
                    int retries = 0;
                    while (status == StatusInfoLengthMismatch && retries++ < 8)
                    {
                        int nextBufferSize = Math.Max(returnLength + 262144, bufferSize * 2);
                        IntPtr newBuffer = Marshal.AllocHGlobal(nextBufferSize);
                        Marshal.FreeHGlobal(buffer);
                        buffer = newBuffer;
                        bufferSize = nextBufferSize;
                        status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, bufferSize, out returnLength);
                    }

                    if (status == 0)
                    {
                        long handleCount = Marshal.ReadInt64(buffer);
                        IntPtr entryPtr = buffer + 16;
                        for (long i = 0; i < handleCount; i++)
                        {
                            long entryPid = Marshal.ReadInt64(entryPtr + 8);
                            long entryHandle = Marshal.ReadInt64(entryPtr + 16);

                            if (entryPid == myPid && entryHandle == handle.ToInt64())
                            {
                                cachedFileObjectTypeIndex = (ushort)Marshal.ReadInt16(entryPtr + 30);
                                break;
                            }
                            entryPtr += 40;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                // Probe failure retains non-negative fallback
            }
            finally
            {
                try { File.Delete(probePath); } catch { }
            }

            if (cachedFileObjectTypeIndex < 0)
            {
                cachedFileObjectTypeIndex = 0; // 0 signals unverified object type (inspect all)
            }

            return cachedFileObjectTypeIndex;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle, IntPtr tgtProc, out IntPtr tgtHandle, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(IntPtr handle, StringBuilder path, uint count, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long created, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
