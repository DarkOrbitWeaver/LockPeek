using LockPeek.Core;
using LockPeek.Windows;
using LockPeek.App;
using System.Diagnostics;
using System.Text;

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception("FAILED: " + name);
    }
    passed++;
    Console.WriteLine("PASS: " + name);
}

var regular = new ProcessEvidence(new(12345, 10), @"C:\Apps\editor.exe", "Editor", true, true, true, false, false, false, false, false, false, true);
Check(SafetyPolicy.Classify(regular).CanClose && SafetyPolicy.Classify(regular).CanForceEnd, "regular app can be closed or force ended");

var nonWindowed = regular with { HasSafeWindow = false };
Check(!SafetyPolicy.Classify(nonWindowed).CanClose && SafetyPolicy.Classify(nonWindowed).CanForceEnd, "non-windowed app allows force end but not normal close");

foreach (var item in new[] { regular with { Verified = false }, regular with { SameUser = false }, regular with { SameSession = false }, regular with { Elevated = true }, regular with { Critical = true }, regular with { Protected = true }, regular with { Service = true }, regular with { WindowsComponent = true }, regular with { SecurityOrHost = true }, regular with { Executable = @"C:\Apps\explorer.exe" }, regular with { Executable = @"C:\Apps\svchost.exe" }, regular with { Identity = new(4, 10) } })
{
    Check(!SafetyPolicy.Classify(item).CanClose && !SafetyPolicy.Classify(item).CanForceEnd, "blocked system process: " + SafetyPolicy.Classify(item).Reason);
}

Check(new HashSet<ProcessIdentity> { new(1, 10), new(1, 11), new(1, 10) }.Count == 2, "deduplicate pids using creation time");

using (var wire = new MemoryStream())
{
    using (var w = new BinaryWriter(wire, Encoding.Unicode, true))
    {
        w.Write(0x314B504Cu);
        w.Write(2u);
        w.Write(3u);
        foreach (var path in new[] { @"C:\unicode\日本語 & space.txt", @"C:\long\" + new string('a', 280) })
        {
            w.Write((uint)path.Length);
            w.Write(Encoding.Unicode.GetBytes(path));
        }
    }
    wire.Position = 0;
    var selection = await SelectionTransport.ReadAsync(wire, default);
    Check(selection.Paths.Length == 2 && selection.Unsupported == 3 && selection.Paths[0].Contains("日本語"), "handles unicode and long paths properly");
}

try
{
    await SelectionTransport.ReadAsync(new MemoryStream([0, 1, 2, 3]), default);
    Check(false, "invalid transfer rejected");
}
catch (IOException)
{
    Check(true, "invalid transfer rejected");
}

using (var wire = new MemoryStream())
{
    using (var writer = new BinaryWriter(wire, Encoding.Unicode, true))
    {
        writer.Write(0x324B504Cu);
        writer.Write(0u);
        writer.Write(100001u);
        writer.Write(0u);
    }
    wire.Position = 0;
    Check((await SelectionTransport.ReadAsync(wire, default)).Unsupported == 100001, "tracks skipped files count");
}

var workflowProbe = new WorkflowProbe(regular);
var workflowTargets = Enumerable.Range(100, 7).Select(pid =>
{
    var evidence = regular with { Identity = new(pid, 10) };
    var detection = new DetectedProcess(evidence.Identity, "Test app", "", 1);
    return new ProcessTarget(detection, evidence, SafetyPolicy.Classify(evidence));
}).ToArray();

var workflowResults = await new ActionWorkflow(workflowProbe).ExecuteAsync(workflowTargets.Concat(workflowTargets), ProcessAction.CloseNormally, false, default);
Check(workflowResults.Count == 7 && workflowProbe.Calls == 6, "bulk close deduplicates pids and rechecks eligibility");
Check(workflowProbe.MaximumActive is > 1 and <= 4 && !workflowProbe.SawForce, "bulk close limits parallel requests and doesnt force close");
Check(workflowResults.Any(r => r.Message.StartsWith("Action could not complete:")) && workflowResults.Count(r => r.Requested) == 5,
    "one app failing doesn't break the other actions");

var unconfirmedProbe = new WorkflowProbe(regular);
try
{
    await new ActionWorkflow(unconfirmedProbe).ExecuteAsync(workflowTargets, ProcessAction.ForceEnd, false, default);
    Check(false, "force confirmation required");
}
catch (InvalidOperationException)
{
    Check(unconfirmedProbe.Calls == 0, "unconfirmed force never reaches process controller");
}

var excludedProbe = new WorkflowProbe(regular);
var excludedResult = await new ActionWorkflow(excludedProbe).ExecuteAsync([workflowTargets[^1]], ProcessAction.ForceEnd, true, default);
Check(excludedProbe.Calls == 0 && !excludedResult[0].Requested, "force end still skips ineligible targets");

Check(new ScanResult([], [new("folder", "Directory handles are not covered", true)], 3, 3, false, TimeSpan.Zero).Complete, "folder notes do not fail the scan");
Check(!new ScanResult([], [new("folder", "Enumeration limit reached")], 3, 3, false, TimeSpan.Zero).Complete, "hitting file limits marks scan as incomplete");

// Real filesystem scenarios are independent of Restart Manager's registry access.
string expansionDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/expansion", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(Path.Combine(expansionDir, "nested"));
string firstPath = Path.Combine(expansionDir, "one.txt");
File.WriteAllText(firstPath, "one");
File.WriteAllText(Path.Combine(expansionDir, "nested", "two.txt"), "two");
var expanded = ResourceExpansion.Expand([expansionDir, firstPath, expansionDir]);
Check(expanded.Paths.Length == 2 && expanded.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "folder expansion deduplicates files properly");
Check(expanded.Issues.Any(i => i.CoverageNote && i.Reason.Contains("folder itself")), "reports folder lock limitation note");

using (var stopWalking = new CancellationTokenSource())
{
    int seen = 0;
    bool cancelled = ResourceExpansion.Visit([expansionDir], _ => { seen++; stopWalking.Cancel(); }, _ => { }, stopWalking.Token);
    Check(cancelled && seen == 1, "folder walk cancels cleanly");
}

using (var cancelledExpansion = new CancellationTokenSource())
{
    cancelledExpansion.Cancel();
    var result = ResourceExpansion.Expand([expansionDir], cancelledExpansion.Token);
    Check(result.Cancelled && result.Paths.Length == 0, "cancelled expansion returns cancelled result");
}

Check(ResourceExpansion.Expand([Path.Combine(expansionDir, "missing.txt")]).Issues.Count == 1, "missing file is reported as issue");
Check(RecyclePolicy.Inspect(@"\\?\" + firstPath).Path == firstPath, "long path prefixes get normalized");

foreach (var invalid in new[] { @"\\?\UNC\server\share\file.txt", @"\\.\PhysicalDrive0", @"\\?\GLOBALROOT\Device\file" })
{
    try
    {
        RecyclePolicy.Inspect(invalid);
        Check(false, "device or network namespace rejected");
    }
    catch (IOException)
    {
        Check(true, "device or network namespace rejected");
    }
}

if (args.Contains("--large-folders"))
{
    string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/large-folder-tests", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(dir);
    Process? largeHolder = null;
    try
    {
        const int total = 10025;
        for (int i = 0; i < total; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"file-{i:D5}.txt"), "");
        }
        int position = 0;
        string? heldFile = null;
        ResourceExpansion.Visit([dir], path => { if (++position == 10001) heldFile = path; }, _ => { });
        Check(heldFile is not null, "test file is encountered beyond the old file limit");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        string holderExe = Path.Combine(root, "tools/HoldFileOpen/bin/Release/net10.0-windows/LockPeek.Holder.exe");
        var start = new ProcessStartInfo(holderExe) { UseShellExecute = false, RedirectStandardOutput = true };
        start.ArgumentList.Add(heldFile!);
        largeHolder = Process.Start(start)!;
        Check(await largeHolder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY", "large-folder holder ready");
        var watch = Stopwatch.StartNew();
        var scan = await new RestartManager().ScanAsync([dir]);
        Check(scan.Complete && scan.Covered == total, "real folder scan processes more than 10,000 files");
        Check(scan.Processes.Any(p => p.Identity.Pid == largeHolder.Id), "large-folder scan finds the real app holding a file beyond 10,000");
        Check(RecyclePolicy.Prepare([dir]).Length == 1, "delete review accepts a folder with more than 10,000 entries");
        string[] selected = Enumerable.Range(0, 1001).Select(i => Path.Combine(dir, $"file-{i:D5}.txt")).ToArray();
        Check(RecyclePolicy.Prepare(selected).Length == selected.Length, "delete review accepts more than 1,000 selected files");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            RecyclePolicy.Prepare([dir], cancelled.Token);
            Check(false, "cancelled delete review");
        }
        catch (OperationCanceledException)
        {
            Check(true, "delete review cancels before any deletion");
        }
        Console.WriteLine($"MEASURE {total:N0} files: {watch.Elapsed.TotalSeconds:N1} s including scan and reviews; test working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:N1} MiB");
    }
    finally
    {
        if (largeHolder is not null)
        {
            if (!largeHolder.HasExited)
            {
                largeHolder.Kill();
                largeHolder.WaitForExit();
            }
            largeHolder.Dispose();
        }
        Directory.Delete(dir, true);
    }
}

using (var modeWire = new MemoryStream())
{
    using (var writer = new BinaryWriter(modeWire, Encoding.Unicode, true))
    {
        writer.Write(0x324B504Cu);
        writer.Write(1u);
        writer.Write(0u);
        writer.Write(1u);
        string resource = @"C:\data\sample.txt";
        writer.Write((uint)resource.Length);
        writer.Write(Encoding.Unicode.GetBytes(resource));
    }
    modeWire.Position = 0;
    Check((await SelectionTransport.ReadAsync(modeWire, default)).RecycleIntent, "shell transfer preserves recycle intent without deleting");
}

string activationPipeName = "LockPeekActivationTest-" + Guid.NewGuid().ToString("N");
using (var server = new System.IO.Pipes.NamedPipeServerStream(activationPipeName, System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
using (var client = new System.IO.Pipes.NamedPipeClientStream(".", activationPipeName, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
{
    var waiting = server.WaitForConnectionAsync();
    await client.ConnectAsync(5000);
    await waiting;
    Check(NativeTestWindow.GetNamedPipeServerProcessId(client.SafePipeHandle, out uint primaryPid) && primaryPid == Environment.ProcessId,
        "pipe handoff gets primary PID");
}

if (args.Contains("--recycle"))
{
    string recycleDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/recycle-tests", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(recycleDir);
    string sample = Path.Combine(recycleDir, "disposable-recycle-test.txt");
    File.WriteAllText(sample, "Disposable LockPeek recycle integration test");
    var prepared = RecyclePolicy.Prepare([sample, sample]);
    Check(prepared.Length == 1, "recycle preparation deduplicates selected resources");
    var recycled = await RecycleBin.MoveAsync(prepared, IntPtr.Zero);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(recycled));
    Check(recycled.Count == 1 && recycled[0].Recycled && !File.Exists(sample), "actual disposable file moved to Recycle Bin with shell callback confirmation");
    string[] batch = Enumerable.Range(0, 5).Select(i => Path.Combine(recycleDir, $"batch-{i}.txt")).ToArray();
    foreach (var path in batch)
    {
        File.WriteAllText(path, "Disposable recycle batch");
    }
    var batchResults = await RecycleBin.MoveAsync(RecyclePolicy.Prepare(batch), IntPtr.Zero);
    Check(batchResults.Count == 5 && batchResults.All(r => r.Deleted && r.Recycled) && batch.All(p => !File.Exists(p)),
        "five real files recycled with accurate per-item outcomes");
    try
    {
        RecyclePolicy.Inspect(Path.GetPathRoot(recycleDir)!);
        Check(false, "drive root rejected");
    }
    catch (IOException)
    {
        Check(true, "drive root excluded from recycling");
    }
    string nested = Path.Combine(recycleDir, "nested");
    Directory.CreateDirectory(nested);
    string nestedFile = Path.Combine(nested, "sample.txt");
    File.WriteAllText(nestedFile, "Disposable nested test");
    Check(RecyclePolicy.Prepare([nested, nestedFile]).Length == 1, "nested recycle selection queues parent only");
    string changed = Path.Combine(recycleDir, "changed.txt");
    File.WriteAllText(changed, "before");
    var staleRecycle = RecyclePolicy.Prepare([changed]);
    File.WriteAllText(changed, "different after confirmation");
    var staleRecycleResult = await RecycleBin.MoveAsync(staleRecycle, IntPtr.Zero);
    Check(!staleRecycleResult[0].Recycled && File.Exists(changed), "changed resource rejected before recycle execution");
    using (var cancelDelete = new CancellationTokenSource())
    {
        var confirmed = RecyclePolicy.Prepare([changed]);
        cancelDelete.Cancel();
        var cancelledDelete = await RecycleBin.MoveAsync(confirmed, IntPtr.Zero, cancelDelete.Token);
        Check(!cancelledDelete[0].Deleted && File.Exists(changed), "cancelled delete preparation does not remove an item");
    }
    using (var cancelAtStart = new CancellationTokenSource())
    {
        var confirmed = RecyclePolicy.Prepare([changed]);
        var cancelledDelete = await RecycleBin.MoveAsync(confirmed, IntPtr.Zero, cancelAtStart.Token, cancelAtStart.Cancel);
        Check(!cancelledDelete[0].Deleted && File.Exists(changed), "cancellation before the shell operation leaves the item in place");
    }
    var callbackFailure = await RecycleBin.MoveAsync(RecyclePolicy.Prepare([changed]), IntPtr.Zero, default,
        () => throw new InvalidOperationException("Disposable callback failure")).WaitAsync(TimeSpan.FromSeconds(5));
    Check(!callbackFailure[0].Deleted && File.Exists(changed), "delete worker completes after a callback failure and preserves the item");
    var nestedResult = await RecycleBin.MoveAsync(RecyclePolicy.Prepare([nested, nestedFile]), IntPtr.Zero);
    Check(nestedResult.All(r => r.Recycled) && !Directory.Exists(nested), "actual folder recycled once with nested selection");
}

if (args.Contains("--recycle-capacity"))
{
    string fixtureDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/recycle-capacity", Guid.NewGuid().ToString("N")));
    Directory.CreateDirectory(fixtureDir);
    string fixture = Path.Combine(fixtureDir, "disposable-oversize-test.bin");
    using (var stream = new FileStream(fixture, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite))
    {
        if (!NativeTestWindow.DeviceIoControl(stream.SafeFileHandle, 0x900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            throw new IOException("Sparse files are unavailable; capacity test was not run.");
        }
        stream.SetLength(1L << 40);
    }
    var checkedItem = RecyclePolicy.Prepare([fixture]);
    var result = await RecycleBin.MoveAsync(checkedItem, IntPtr.Zero);
    bool stillExists = File.Exists(fixture);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    Check(stillExists || result[0].Recycled, "oversized disposable item is recycled or remains; no permanent deletion");
}

if (args.Contains("--integration"))
{
    string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    string holder = Path.Combine(root, "tools/HoldFileOpen/bin/Release/net10.0-windows/LockPeek.Holder.exe");
    string dir = Path.Combine(root, "artifacts/integration", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string[] paths = Enumerable.Range(0, 3).Select(i => Path.Combine(dir, $"日本語 file {i}.txt")).ToArray();
    foreach (var p in paths)
    {
        File.WriteAllText(p, "Disposable integration resource");
    }
    var children = new List<Process>();
    async Task<Process> Start(bool ignore = false)
    {
        var psi = new ProcessStartInfo(holder) { UseShellExecute = false, RedirectStandardOutput = true };
        if (ignore) psi.ArgumentList.Add("--ignore-close");
        foreach (var path in paths) psi.ArgumentList.Add(path);
        var child = Process.Start(psi)!;
        children.Add(child);
        Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)) == "READY", "disposable GUI holder ready");
        return child;
    }
    try
    {
        var first = await Start();
        var second = await Start(true);
        first.Refresh();
        var fixtureWindow = first.MainWindowHandle;
        Check(fixtureWindow != IntPtr.Zero, "disposable holder exposes a real window for activation tests");
        NativeTestWindow.ShowWindow(fixtureWindow, 6);
        Check(NativeTestWindow.IsIconic(fixtureWindow), "disposable fixture window minimized");
        WindowActivation.RestoreAndActivate(fixtureWindow);
        Check(!NativeTestWindow.IsIconic(fixtureWindow), "activation helper restores a minimized real window");
        var scanner = new RestartManager();
        var control = new ProcessController();
        var scan = await scanner.ScanAsync(paths.Concat(paths));
        if (!scan.Complete || scan.Covered != 3)
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(scan));
        Check(scan.Complete && scan.Covered == 3, "real RM multiple resources and duplicate input coverage");
        var targets = scan.Processes.Where(p => p.Identity.Pid == first.Id || p.Identity.Pid == second.Id).ToArray();
        Check(targets.Length == 2, "two real processes using three files appear once each");
        var folderScan = await scanner.ScanAsync([dir, paths[0]]);
        Check(folderScan.Covered == 3 && folderScan.Processes.Count(p => p.Identity.Pid == first.Id || p.Identity.Pid == second.Id) == 2,
            "folder plus overlapping file detects both holders once");
        Check(folderScan.Complete && folderScan.Issues.Any(i => i.Reason.Contains("folder itself")), "folder-handle coverage limitation is explicit");
        var nested = Path.Combine(dir, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "nested.txt"), "nested");
        Check((await scanner.ScanAsync([dir])).Covered == 4, "recursive folder scan covers nested files");
        File.Delete(Path.Combine(nested, "nested.txt"));
        Directory.Delete(nested);
        var directScanner = new DirectHandleScanner();
        var directScan = await directScanner.ScanAsync(paths.Concat(paths));
        Check(directScan.Complete && directScan.Covered == 3, "direct handle scanner covers multiple targets with duplicate inputs");
        Check(directScan.Processes.Count(p => p.Identity.Pid == first.Id || p.Identity.Pid == second.Id) == 2, "direct handle scanner detects both real processes");
        var directFolderScan = await directScanner.ScanAsync([dir]);
        Check(directFolderScan.Complete && directFolderScan.Processes.Count(p => p.Identity.Pid == first.Id || p.Identity.Pid == second.Id) == 2,
            "direct handle folder scan detects both holders without file recursion");
        var target1 = control.Inspect(targets.Single(p => p.Identity.Pid == first.Id));
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(target1));
        Check(target1.Eligibility.CanClose && target1.Eligibility.CanForceEnd, "real same-user GUI holder eligible");
        var stale = target1 with { Detection = target1.Detection with { Identity = target1.Detection.Identity with { CreationTime = 1 } } };
        Check(!(await control.ActAsync(stale, ProcessAction.ForceEnd, default)).Requested && !first.HasExited, "stale PID identity rejects force end on real process");
        Check((await control.ActAsync(target1, ProcessAction.CloseNormally, default)).Requested, "real graceful WM_CLOSE requested");
        Check(first.WaitForExit(5000), "ordinary holder exited gracefully");
        var target2 = control.Inspect(targets.Single(p => p.Identity.Pid == second.Id));
        await control.ActAsync(target2, ProcessAction.CloseNormally, default);
        Check(!second.HasExited, "normal-close refusal never escalates to force");
        Check((await control.ActAsync(target2, ProcessAction.ForceEnd, default)).Requested && second.WaitForExit(5000), "explicit force end of real disposable app");
        Check(!(await scanner.ScanAsync(paths)).Processes.Any(p => p.Identity.Pid == first.Id || p.Identity.Pid == second.Id), "real rescan observes both holders gone");
        var partial = await scanner.ScanAsync([paths[0], dir, Path.Combine(dir, "missing.txt")]);
        Check(!partial.Complete && partial.Covered == 3 && partial.Issues.Count == 2, "folder expansion deduplicates files and reports directory-handle limitation and missing resource");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Check((await scanner.ScanAsync(paths, null, cancel.Token)).Cancelled, "cancelled scan reports incomplete");
        var third = await Start(true);
        var fourth = await Start(true);
        var bulkScan = await scanner.ScanAsync(paths);
        var bulk = bulkScan.Processes.Where(p => p.Identity.Pid == third.Id || p.Identity.Pid == fourth.Id).Select(control.Inspect).ToArray();
        Check(bulk.Length == 2 && bulk.All(t => t.Eligibility.CanForceEnd), "two real ordinary GUI apps eligible for bulk force end");
        var workflow = new ActionWorkflow(control);
        try
        {
            await workflow.ExecuteAsync(bulk, ProcessAction.ForceEnd, false, default);
            Check(false, "unconfirmed force rejected");
        }
        catch (InvalidOperationException)
        {
            Check(!third.HasExited && !fourth.HasExited, "unconfirmed bulk force rejected without terminating anything");
        }
        var bulkOutcomes = await workflow.ExecuteAsync(bulk.Concat(bulk), ProcessAction.ForceEnd, true, default);
        Check(bulkOutcomes.Count == 2 && bulkOutcomes.All(o => o.Requested) && third.WaitForExit(5000) && fourth.WaitForExit(5000), "confirmed bulk force ends both disposable apps exactly once");
        var self = Process.GetCurrentProcess();
        var selfTarget = control.Inspect(new(new(self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc()), self.ProcessName, "", 1));
        Check(!selfTarget.Eligibility.CanForceEnd && !selfTarget.Eligibility.CanClose, "live self-process excluded");
        var excluded = await workflow.ExecuteAsync([selfTarget], ProcessAction.ForceEnd, true, default);
        Check(excluded.Count == 1 && !excluded[0].Requested, "bulk workflow preserves live excluded target");
        using (var shell = Process.GetProcessesByName("explorer").FirstOrDefault())
        {
            if (shell is not null)
            {
                var shellTarget = control.Inspect(new(new(shell.Id, shell.StartTime.ToUniversalTime().ToFileTimeUtc()), "explorer.exe", "", 4));
                Check(!shellTarget.Eligibility.CanClose && !shellTarget.Eligibility.CanForceEnd, "live Explorer shell excluded (no action attempted)");
            }
            else Console.WriteLine("UNVERIFIED live Explorer exclusion: no Explorer process available");
        }
        var many = Enumerable.Range(0, 1150).Select(i => Path.Combine(dir, $"batch-{i}.txt")).ToArray();
        foreach (var path in many)
        {
            File.WriteAllText(path, "batch resource");
        }
        using var halfway = new CancellationTokenSource();
        var midway = await scanner.ScanAsync(many, new ImmediateProgress(p => halfway.Cancel()), halfway.Token);
        Check(midway.Cancelled && midway.Covered == 1024 && !midway.Complete, "mid-scan cancellation preserves first batch and reports partial coverage");
        using (var releaseScan = new ManualResetEventSlim())
        using (var cancelQueued = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = scanner.ScanAsync(many, new ImmediateProgress(_ =>
            {
                entered.TrySetResult();
                releaseScan.Wait(TimeSpan.FromSeconds(15));
            }));
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
                var queued = scanner.ScanAsync(paths, null, cancelQueued.Token);
                cancelQueued.Cancel();
                Check((await queued).Cancelled, "replacement scan cancels while the previous scan is still running");
            }
            finally
            {
                releaseScan.Set();
                await running;
            }
        }
        Console.WriteLine($"MEASURE aggregate scan {scan.Elapsed.TotalMilliseconds:F1} ms; test working set {Process.GetCurrentProcess().WorkingSet64 / 1048576.0:F1} MiB");
    }
    finally
    {
        foreach (var child in children)
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
            child.Dispose();
        }
    }
}
Console.WriteLine($"{passed} checks passed.");

sealed class ImmediateProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
{
    public void Report(ScanProgress value) => report(value);
}

sealed class WorkflowProbe(ProcessEvidence regular) : IProcessController
{
    private int active;
    public int Calls, MaximumActive;
    public bool SawForce;
    public ProcessTarget Inspect(DetectedProcess process)
    {
        var evidence = regular with { Identity = process.Identity, SameUser = process.Identity.Pid != 106 };
        return new(process, evidence, SafetyPolicy.Classify(evidence));
    }
    public async Task<ActionOutcome> ActAsync(ProcessTarget target, ProcessAction action, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Calls);
        if (action == ProcessAction.ForceEnd) SawForce = true;
        int count = Interlocked.Increment(ref active);
        int seen;
        do
        {
            seen = MaximumActive;
        } while (count > seen && Interlocked.CompareExchange(ref MaximumActive, count, seen) != seen);
        try
        {
            await Task.Delay(40, cancellation);
            if (target.Detection.Identity.Pid == 105) throw new IOException("Test action failure");
            return new(target.Detection.Identity, target.Detection.Name, "Test close requested", true);
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }
}
