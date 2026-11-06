using LockPeek.Core;
using Microsoft.UI.Xaml;

namespace LockPeek.App;

public sealed record OutcomeRow(string Name, string Message, string StatusText, bool Success = false, bool Waiting = false)
{
    public ProcessRow? SourceRow { get; init; }
    public string? ItemGlyph { get; init; }
    public string? ItemPath { get; init; }
    public bool IsFirst { get; set; }
    public Thickness SeparatorThickness => IsFirst ? new Thickness(0) : new Thickness(0, 1, 0, 0);
    public Visibility ItemIconVisibility => ItemGlyph is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AppIconVisibility => ItemGlyph is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MessageVisibility => string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;

    public OutcomeRow(ActionOutcome outcome) : this(
        outcome.Name,
        outcome.Exited ? "" : outcome.Message,
        outcome.Exited ? "Ended" : outcome.Requested ? "Still running" : outcome.Message.StartsWith("Skipped:") ? "Skipped" : "Not completed",
        outcome.Exited,
        !outcome.Exited && outcome.Requested)
    {
    }

    public Visibility EndedVisibility => Success ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisibility => Waiting ? Visibility.Visible : Visibility.Collapsed;
    public Visibility OtherVisibility => !Success && !Waiting ? Visibility.Visible : Visibility.Collapsed;

    public string AutomationName => ToString();

    public override string ToString() =>
        $"{Name}: {StatusText}{(string.IsNullOrEmpty(Message) ? "" : $" ({Message})")}";
}
