using LockPeek.Core;
using Microsoft.UI.Xaml;

namespace LockPeek.App;

public sealed record ProcessRow(ProcessTarget Target) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    public Microsoft.UI.Xaml.Media.ImageSource? Icon { get; private set; }

    public async Task LoadIconAsync()
    {
        if (!Target.Evidence.Verified) return;
        try
        {
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(Executable);
            using var image = await file.GetThumbnailAsync(global::Windows.Storage.FileProperties.ThumbnailMode.ListView, 32);
            if (image is null) return;
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(image);
            Icon = bitmap;
            PropertyChanged?.Invoke(this, new(nameof(Icon)));
            PropertyChanged?.Invoke(this, new(nameof(IconFallbackVisibility)));
        }
        catch (Exception)
        {
            // Retain default fallback icon on metadata lookup error
        }
    }

    public string Name => Target.Evidence.DisplayName;
    public Visibility IconFallbackVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string Subtitle => "Using one or more selected files";
    public Visibility WarningVisibility => Target.Eligibility.CanClose ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExcludedVisibility => Target.Eligibility.CanClose ? Visibility.Collapsed : Visibility.Visible;
    public string ProcessId => $"Process ID: {Target.Detection.Identity.Pid}";
    public string Executable => Target.Evidence.Executable;
    public string IdentityDetails => Target.Evidence.Verified
        ? $"User: {Target.Evidence.Owner ?? "Unavailable"} · Session: {Target.Evidence.SessionId} · {(Target.Evidence.Elevated ? "Administrator" : "Standard permissions")}"
        : "Process identity could not be verified";
    public string Reason => Target.Eligibility.Reason;

    public string AutomationName => ToString();

    public override string ToString()
    {
        string status = Target.Eligibility.CanClose
            ? "Eligible to close"
            : Target.Eligibility.CanForceEnd
                ? "Eligible for Force End"
                : $"Excluded: {Target.Eligibility.Reason}";
        return $"{Name}, Process ID {Target.Detection.Identity.Pid}, {status}";
    }
}
