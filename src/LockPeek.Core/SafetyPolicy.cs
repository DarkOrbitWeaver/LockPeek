namespace LockPeek.Core;

public static class SafetyPolicy
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "registry", "idle", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "explorer.exe", "dwm.exe",
        "fontdrvhost.exe", "sihost.exe", "taskhostw.exe", "runtimebroker.exe", "dllhost.exe",
        "rundll32.exe", "conhost.exe", "securityhealthservice.exe", "msmpeng.exe", "mssense.exe",
        "nissrv.exe", "searchindexer.exe", "applicationframehost.exe", "lockpeek.exe"
    };

    public static Eligibility Classify(ProcessEvidence e)
    {
        if (e.Identity.Pid <= 4)
            return new(false, false, "Essential Windows process");

        if (!e.Verified)
            return new(false, false, e.Error ?? "Process identity or access could not be verified");

        if (!e.SameUser)
            return new(false, false, "Belongs to another user");

        if (!e.SameSession)
            return new(false, false, "Runs in another session");

        if (e.Critical || e.Protected)
            return new(false, false, "Critical or protected Windows process");

        if (e.Service)
            return new(false, false, "Windows service; excluded from actions");

        if (e.WindowsComponent)
            return new(false, false, "Windows component; excluded from actions");

        if (e.SecurityOrHost || Excluded.Contains(Path.GetFileName(e.Executable)))
            return new(false, false, "Security, shell or shared host process");

        if (e.Elevated)
            return new(false, false, "Elevated process; excluded from actions");

        // Non-windowed user processes (tray apps, background agents, console tools)
        // cannot receive WM_CLOSE, but are eligible for explicit Force End.
        if (!e.HasSafeWindow)
            return new(false, true, "No standard window; requires Force End");

        return new(true, true, "May have unsaved work");
    }

    public static IReadOnlyList<ProcessTarget> Review(IEnumerable<ProcessTarget> selected, ProcessAction action) =>
        selected.Where(p => action == ProcessAction.CloseNormally ? !p.Eligibility.CanClose : !p.Eligibility.CanForceEnd).ToArray();
}
