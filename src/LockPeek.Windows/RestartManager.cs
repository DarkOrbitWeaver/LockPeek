using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LockPeek.Core;

namespace LockPeek.Windows;

public sealed class RestartManager(int batchSize = 1024) : IResourceScanner
{
    private readonly int resourceBatchSize = batchSize > 0 ? batchSize : throw new ArgumentOutOfRangeException(nameof(batchSize));
    private readonly SemaphoreSlim scanGate = new(1, 1);
    public async Task<ScanResult> ScanAsync(IEnumerable<string> paths, IProgress<ScanProgress>? progress = null, CancellationToken cancellation = default)
    {
        var input = paths.ToArray();
        try { await scanGate.WaitAsync(cancellation); }
        catch (OperationCanceledException) { return new([], [], 0, 0, true, TimeSpan.Zero); }
        try { return await Task.Run(() => Scan(input, progress, cancellation)); }
        finally { scanGate.Release(); }
    }
    private ScanResult Scan(string[] input, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        var issues = new List<ScanIssue>();
        var found = new Dictionary<ProcessIdentity, DetectedProcess>();
        int covered = 0, requested = 0, omittedIssues = 0, omittedNotes = 0; bool cancelled = false;
        var batch = new List<string>(resourceBatchSize);
        void AddIssue(ScanIssue issue)
        {
            if (issues.Count < 1000) issues.Add(issue);
            else if (issue.CoverageNote) omittedNotes++; else omittedIssues++;
        }
        // Independent sessions bound registry cost and preserve already completed coverage on failures.
        void ScanBatch(string[] batch, int splitDepth = 0)
        {
            if (token.IsCancellationRequested) { cancelled = true; return; }
            uint session = 0; bool started = false;
            try
            {
                Check(RmStartSession(out session, 0, new StringBuilder(33))); started = true;
                Check(RmRegisterResources(session, (uint)batch.Length, batch, 0, IntPtr.Zero, 0, IntPtr.Zero));
                uint needed = 0, count = 0;
                int error = RmGetList(session, out needed, ref count, null, out _);
                RM_PROCESS_INFO[]? buffer = null;
                for (int retry = 0; error == 234 && retry < 8; retry++)
                {
                    if (token.IsCancellationRequested) { cancelled = true; break; }
                    if (needed > 65536) throw new IOException("Restart Manager returned an excessive process count");
                    count = needed + 8; buffer = new RM_PROCESS_INFO[count];
                    error = RmGetList(session, out needed, ref count, buffer, out _);
                }
                if (cancelled) return;
                Check(error);
                if (buffer is not null)
                    foreach (var item in buffer.Take((int)count))
                    {
                        var id = new ProcessIdentity(item.Process.Pid, item.Process.Start);
                        found[id] = new(id, item.Name, item.Service, item.Type);
                    }
                covered += batch.Length;
                progress?.Report(new(covered, 0));
            }
            catch (Exception e) when (e is Win32Exception or IOException)
            {
                // End this session before retrying smaller groups. One unsupported resource must not hide the others.
                // A registry mutex timeout is a session-wide failure, not evidence
                // that one registered path caused it. Splitting only repeats it.
                bool canIsolateResource = started && e is not Win32Exception { NativeErrorCode: 121 };
                if (started) { RmEndSession(session); started = false; }
                if (canIsolateResource && batch.Length > 1 && !token.IsCancellationRequested)
                {
                    if (splitDepth < 4)
                    {
                        int middle = batch.Length / 2;
                        ScanBatch(batch[..middle], splitDepth + 1);
                        ScanBatch(batch[middle..], splitDepth + 1);
                    }
                    else
                    {
                        // Direct per-item isolation prevents deep recursive session cascades
                        foreach (var path in batch)
                        {
                            if (token.IsCancellationRequested) { cancelled = true; break; }
                            ScanBatch([path], splitDepth + 1);
                        }
                    }
                }
                else foreach (var path in batch)
                {
                    string explanation = path.Length >= 260 && e is Win32Exception { NativeErrorCode: 29 }
                        ? "This file couldn't be checked. Its path may be too long."
                        : "Windows couldn't check this file. Try scanning again.";
                    bool isLongPath = path.Length >= 260 && e is Win32Exception { NativeErrorCode: 29 };
                    AddIssue(new(path, explanation, isLongPath));
                }
            }
            finally { if (started) RmEndSession(session); }
        }
        cancelled = ResourceExpansion.Visit(input, path =>
        {
            requested++;
            // Long paths may be rejected by Restart Manager on some systems.
            // Try each one, but isolate it so a failed registration doesn't
            // repeatedly split a batch of otherwise checkable files.
            if (path.Length >= 260)
            {
                ScanBatch([path]);
                return;
            }
            batch.Add(path);
            if (batch.Count != resourceBatchSize) return;
            ScanBatch(batch.ToArray());
            batch.Clear();
        }, issue =>
        {
            if (!issue.CoverageNote) requested++;
            AddIssue(issue);
        }, token);
        if (!cancelled && !token.IsCancellationRequested && batch.Count > 0) ScanBatch(batch.ToArray());
        if (omittedIssues > 0) issues.Add(new("Selection", $"{omittedIssues:N0} additional coverage messages omitted."));
        if (omittedNotes > 0) issues.Add(new("Selection", $"{omittedNotes:N0} additional folder notes omitted.", true));
        // Cancellation is cooperative between synchronous documented native calls; no abandoned sessions.
        cancelled |= token.IsCancellationRequested;
        return new(found.Values.OrderBy(x => x.Name).ToArray(), issues, requested, covered, cancelled, watch.Elapsed);
    }
    private static void Check(int error) { if (error != 0) throw new Win32Exception(error); }
    // FILETIME consists of two DWORDs and is 4-byte aligned, including on x64.
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct RM_UNIQUE_PROCESS { public int Pid; public long Start; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public int Type; public uint Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint handle, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint handle, uint count, string[] files, uint apps, IntPtr processes, uint services, IntPtr serviceNames);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[]? info, out uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint handle);
}
