namespace LockPeek.Core;

public sealed class ActionWorkflow(IProcessController controller)
{
    public async Task<IReadOnlyList<ActionOutcome>> ExecuteAsync(
        IEnumerable<ProcessTarget> selection,
        ProcessAction action,
        bool forceConfirmed,
        CancellationToken token)
    {
        if (action == ProcessAction.ForceEnd && !forceConfirmed)
        {
            throw new InvalidOperationException("Force End requires explicit user confirmation");
        }

        var targets = selection.DistinctBy(t => t.Detection.Identity).ToArray();
        var outcomes = new ActionOutcome[targets.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Length),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            async (index, cancellation) =>
            {
                var target = targets[index];
                try
                {
                    var fresh = controller.Inspect(target.Detection);
                    bool eligible = action == ProcessAction.ForceEnd
                        ? fresh.Eligibility.CanForceEnd
                        : fresh.Eligibility.CanClose;

                    if (eligible)
                    {
                        outcomes[index] = await controller.ActAsync(fresh, action, cancellation);
                    }
                    else
                    {
                        outcomes[index] = new(fresh.Detection.Identity, fresh.Detection.Name, "Skipped: " + fresh.Eligibility.Reason, false);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error)
                {
                    outcomes[index] = new(target.Detection.Identity, target.Detection.Name, "Action could not complete: " + error.Message, false);
                }
            });

        return outcomes;
    }
}
