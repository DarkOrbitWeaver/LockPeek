namespace LockPeek.Core;

public readonly record struct ProcessIdentity(int Pid, long CreationTime);
public sealed record DetectedProcess(ProcessIdentity Identity, string Name, string Service, int ApplicationType);
public sealed record ScanIssue(string Resource, string Reason, bool CoverageNote = false);
public sealed record ScanProgress(int Completed, int Total);
public sealed record ScanResult(IReadOnlyList<DetectedProcess> Processes, IReadOnlyList<ScanIssue> Issues,
    int Requested, int Covered, bool Cancelled, TimeSpan Elapsed)
{
    public bool Complete => !Cancelled && Issues.All(i => i.CoverageNote) && Requested == Covered;
}
public sealed record ProcessEvidence(ProcessIdentity Identity, string Executable, string DisplayName,
    bool Verified, bool SameUser, bool SameSession, bool Elevated, bool Critical, bool Protected,
    bool Service, bool WindowsComponent, bool SecurityOrHost, bool HasSafeWindow, string? Error = null, string? Owner = null, int? SessionId = null);
public sealed record Eligibility(bool CanClose, bool CanForceEnd, string Reason);
public sealed record ProcessTarget(DetectedProcess Detection, ProcessEvidence Evidence, Eligibility Eligibility);
public enum ProcessAction { CloseNormally, ForceEnd }
public sealed record ActionOutcome(ProcessIdentity Identity, string Name, string Message, bool Requested, bool Exited = false);
public interface IResourceScanner
{
    Task<ScanResult> ScanAsync(IEnumerable<string> paths, IProgress<ScanProgress>? progress, CancellationToken cancellation);
}
public interface IProcessController
{
    ProcessTarget Inspect(DetectedProcess process);
    Task<ActionOutcome> ActAsync(ProcessTarget target, ProcessAction action, CancellationToken cancellation);
}
