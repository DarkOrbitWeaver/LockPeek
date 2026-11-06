using LockPeek.Windows;
using System.Text.Json;
if (args.Length == 2 && args[0] == "--benchmark")
{
    int files = 0;
    var walk = System.Diagnostics.Stopwatch.StartNew();
    ResourceExpansion.Visit([args[1]], _ => files++, _ => { });
    Console.WriteLine($"Enumeration: {files} files in {walk.Elapsed.TotalMilliseconds:F1} ms");
    foreach (int size in new[] { 128, 1024, 4096 })
    {
        int batches = 0;
        var result = await new RestartManager(size).ScanAsync([args[1]], new BatchProgress(() => batches++));
        Console.WriteLine(JsonSerializer.Serialize(new { BatchSize = size, Batches = batches, result.Covered, result.Complete,
            Milliseconds = result.Elapsed.TotalMilliseconds, Processes = result.Processes.Count, Issues = result.Issues.Count }));
    }
    return 0;
}
var scan = await new DirectHandleScanner().ScanAsync(args);
Console.WriteLine(JsonSerializer.Serialize(scan, new JsonSerializerOptions { WriteIndented = true }));
return scan.Complete ? 0 : 2;
sealed class BatchProgress(Action report) : IProgress<LockPeek.Core.ScanProgress>
{
    public void Report(LockPeek.Core.ScanProgress progress) => report();
}
