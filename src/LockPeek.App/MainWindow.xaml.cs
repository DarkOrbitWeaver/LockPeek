using LockPeek.Core;
using LockPeek.Windows;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace LockPeek.App;
public sealed partial class MainWindow : Window
{
    private readonly DirectHandleScanner scanner = new();
    private readonly ProcessController controller = new();
    private readonly HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? scanCancellation;
    private CancellationTokenSource? deleteReviewCancellation;
    private bool lastScanComplete;
    private int thanksGeneration;
    private bool busy;
    private bool acting;
    private bool deleteIntent;
    private bool landingActive;
    private bool startedFromLanding;
    private bool updatingSelection;
    private int unsupported;
    private int generation;
    private FileSelection? pending;
    private OutcomeRow[]? allDeletionOutcomes;
    private OutcomeRow? firstOutcome;
    private int outcomeSearchGeneration;
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32((int)(720 * scale), (int)(400 * scale)));

        Headline.Visibility = Visibility.Collapsed;
        FileSummary.Visibility = Visibility.Collapsed;
        Root.ActualThemeChanged += (_, _) => UpdateWindowIcon();
        Root.Loaded += (_, _) => UpdateWindowIcon();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(640 * scale);
            presenter.PreferredMinimumHeight = (int)(400 * scale);
        }
        Closed += (_, _) =>
        {
            generation++;
            scanCancellation?.Cancel();
            deleteReviewCancellation?.Cancel();
        };
    }
    public void ShowFromExplorer()
    {
        var handle = WindowNative.GetWindowHandle(this);
        WindowActivation.RestoreAndActivate(handle);
        Activate();
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private void GrowForReport(int rowCount, bool showSearch = false)
    {
        if (AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Restored }) return;
        double scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        // Show up to eight collapsed rows, then scroll; stay comfortably below
        // the monitor's usable height and preserve a larger user-sized window.
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int preferredHeight = (int)(Math.Clamp(200 + Math.Min(rowCount, 8) * 48 + (showSearch ? 44 : 0), 400, 600) * scale);
        int height = Math.Min(preferredHeight, Math.Max((int)(400 * scale), (int)(workArea.Height * 0.8)));
        if (height > AppWindow.Size.Height)
        {
            int top = Math.Max(workArea.Y, Math.Min(AppWindow.Position.Y, workArea.Y + workArea.Height - height));
            AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(AppWindow.Position.X, top, AppWindow.Size.Width, height));
        }
    }
    private void UpdateWindowIcon()
    {
        string variant = Root.ActualTheme == ElementTheme.Dark ? "dark" : "light";
        string icon = Path.Combine(AppContext.BaseDirectory, "assets", "taskbar", $"lockpeek-{variant}.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        string png = Path.Combine(AppContext.BaseDirectory, "assets", "taskbar", $"lockpeek-{variant}-24.png");
        if (File.Exists(png)) TitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(png));
        else TitleIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri($"ms-appx:///assets/taskbar/lockpeek-{variant}.svg"));
    }
    public void ReportLaunchError(string message) { Status.Text = "Selection could not be received: " + message; }
    public void Receive(FileSelection selection)
    {
        if (acting)
        {
            bool replaced = pending is not null;
            pending = selection;
            Status.Text = replaced ? "New selection replaced the previous queued selection." : "New selection queued until the current action finishes.";
            return;
        }
        if (selection.Paths.Length == 0 && selection.Unsupported == 0)
        {
            if (files.Count == 0 && !busy) ShowLanding();
            return;
        }
        deleteIntent = selection.RecycleIntent;
        startedFromLanding = false;
        Recycle.Visibility = Visibility.Collapsed;
        RecycleHint.Visibility = deleteIntent ? Visibility.Visible : Visibility.Collapsed;
        // A new Explorer invocation is one selection and one aggregate scan.
        files.Clear();
        foreach (var path in selection.Paths)
        {
            files.Add(path);
        }
        unsupported = selection.Unsupported;
        _ = ScanAsync();
    }
    private async void PickFiles(object sender, RoutedEventArgs e)
    {
        if (!landingActive || acting) return;
        try
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var chosen = await picker.PickMultipleFilesAsync();
            if (!landingActive || chosen.Count == 0) return;
            foreach (var file in chosen) { if (!string.IsNullOrWhiteSpace(file.Path)) files.Add(file.Path); }
            UpdateLanding();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private async void PickFolder(object sender, RoutedEventArgs e)
    {
        if (!landingActive || acting) return;
        try
        {
            var picker = new FolderPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (!landingActive) return;
            if (folder is not null && !string.IsNullOrWhiteSpace(folder.Path)) files.Add(folder.Path);
            UpdateLanding();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private void ShowLanding()
    {
        scanCancellation?.Cancel();
        generation++;
        busy = false;
        landingActive = true;
        deleteIntent = false;
        unsupported = 0;
        Landing.Visibility = Visibility.Visible;
        Headline.Visibility = SelectionHeader.Visibility = FileSummary.Visibility = Visibility.Collapsed;
        ResultsCard.Visibility = OutcomeView.Visibility = EmptyState.Visibility = Coverage.Visibility = Visibility.Collapsed;
        NormalClose.Visibility = ForceEnd.Visibility = Done.Visibility = ReviewRemaining.Visibility = CancelScan.Visibility = Visibility.Collapsed;
        Progress.Visibility = Visibility.Collapsed;
        ActionBar.Visibility = Visibility.Visible;
        Status.Text = "";
        UpdateLanding();
        UpdateActions();
    }
    private void UpdateLanding()
    {
        bool hasItems = files.Count > 0;
        LandingSummary.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
        LandingPreview.Visibility = ClearSelection.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        StartScan.IsEnabled = hasItems;
        PopulateSelectionSummary(LandingItems, generation);
    }
    private void ClearSelectionClick(object sender, RoutedEventArgs e)
    {
        files.Clear();
        unsupported = 0;
        UpdateLanding();
    }
    private async void StartScanClick(object sender, RoutedEventArgs e)
    {
        if (landingActive && files.Count > 0)
        {
            startedFromLanding = true;
            await ScanAsync();
        }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool allowed = !acting && e.DataView.Contains(StandardDataFormats.StorageItems);
        e.AcceptedOperation = allowed ? DataPackageOperation.Copy : DataPackageOperation.None;
        DropOutline.StrokeThickness = (allowed && landingActive) ? 2 : 1;
    }
    private void OnDragLeave(object sender, DragEventArgs e) => DropOutline.StrokeThickness = 1;
    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOutline.StrokeThickness = 1;
        if (acting) return;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var droppedPaths = new List<string>();
            int unsupp = 0;
            foreach (var item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.Path))
                    droppedPaths.Add(item.Path);
                else
                    unsupp++;
            }

            if (droppedPaths.Count > 0)
            {
                if (landingActive && files.Count == 0)
                {
                    foreach (var p in droppedPaths) files.Add(p);
                    unsupported += unsupp;
                    UpdateLanding();
                }
                else
                {
                    Receive(new FileSelection(droppedPaths.ToArray(), unsupp));
                }
            }
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
        }
    }
    private void UpdateSelectionHeader(int scanGeneration)
    {
        PopulateSelectionSummary(FileSummary, scanGeneration);
    }
    private void PopulateSelectionSummary(StackPanel container, int scanGeneration)
    {
        container.Children.Clear();
        foreach (string path in files.Take(2))
        {
            var icon = new FontIcon { Glyph = "\uE8A5", FontSize = 14, Width = 20, Height = 20 };
            var iconSlot = new Grid { Width = 20, Height = 20 };
            iconSlot.Children.Add(icon);
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            entry.Children.Add(iconSlot);
            double nameWidth = container == LandingItems ? files.Count > 2 ? 80 : files.Count == 2 ? 120 : 190 : 190;
            entry.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(name) ? path : name, FontSize = 13, MaxWidth = nameWidth, TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTipService.SetToolTip(entry, path);
            container.Children.Add(entry);
            _ = LoadSelectionIconAsync(path, iconSlot, icon, scanGeneration);
        }
        if (files.Count > 2)
        {
            var more = new HyperlinkButton { Content = $"and {files.Count - 2} more", FontSize = 13, Padding = new Thickness(0) };
            more.Click += ShowSelectedFiles;
            container.Children.Add(more);
        }
    }
    private async void ShowSelectedFiles(object sender, RoutedEventArgs e)
    {
        var selection = files.ToArray();
        var search = new TextBox { PlaceholderText = "Find a file or folder" };
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, ItemsSource = selection };
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Enabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollMode(list, ScrollMode.Enabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        CancellationTokenSource? searchCancellation = null;
        search.TextChanged += async (_, _) =>
        {
            searchCancellation?.Cancel();
            var cancellation = new CancellationTokenSource();
            searchCancellation = cancellation;
            var token = cancellation.Token;
            string query = search.Text;
            try
            {
                await Task.Delay(250, token);
                var matches = await Task.Run(() => selection.Where(path =>
                {
                    token.ThrowIfCancellationRequested();
                    return path.Contains(query, StringComparison.OrdinalIgnoreCase);
                }).ToArray(), token);
                if (!token.IsCancellationRequested) list.ItemsSource = matches;
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(searchCancellation, cancellation)) searchCancellation = null;
                cancellation.Dispose();
            }
        };
        var content = new Grid { Height = Math.Clamp(Root.ActualHeight - 180, 120, 380), RowSpacing = 12 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(search);
        Grid.SetRow(list, 1);
        content.Children.Add(list);
        try { await new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"{selection.Length} selected items", Content = content, CloseButtonText = "Done" }.ShowAsync(); }
        finally { searchCancellation?.Cancel(); }
    }
    private async Task LoadSelectionIconAsync(string path, Grid slot, FontIcon fallback, int scanGeneration)
    {
        try
        {
            var attributes = await Task.Run(() => File.GetAttributes(path));
            bool folder = (attributes & FileAttributes.Directory) != 0;
            if (scanGeneration != generation) return;
            fallback.Glyph = folder ? "\uE8B7" : "\uE8A5";
            // File-type icons stay small. Never request document previews or hydrate cloud files.
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0 || folder || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\")) return;
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(global::Windows.Storage.FileProperties.ThumbnailMode.ListView, 16);
            if (thumbnail is null || scanGeneration != generation) return;
            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            if (scanGeneration != generation) return;
            slot.Children.Clear();
            slot.Children.Add(new Image { Source = bitmap, Width = 16, Height = 16 });
        }
        catch (Exception) { /* Missing/unavailable selections retain the generic file icon. */ }
    }
    private async Task ScanAsync(bool afterAction = false)
    {
        landingActive = false;
        Landing.Visibility = Visibility.Collapsed;
        lastScanComplete = false;
        Coverage.IsExpanded = false;
        scanCancellation?.Cancel();
        var ownCancellation = new CancellationTokenSource();
        scanCancellation = ownCancellation;
        int ownGeneration = ++generation;
        busy = true;
        ProcessList.ItemsSource = null;
        SelectAll.IsChecked = false;
        UpdateActions();
        Headline.Visibility = Visibility.Visible;
        FileSummary.Visibility = Visibility.Visible;
        EmptyState.Visibility = ResultsCard.Visibility = OutcomeView.Visibility = Coverage.Visibility = Visibility.Collapsed;
        NormalClose.Visibility = ForceEnd.Visibility = Visibility.Visible;
        Done.Visibility = ReviewRemaining.Visibility = Visibility.Collapsed;
        AddFiles.Visibility = Visibility.Visible;
        ActionBar.Visibility = Visibility.Visible;
        AddFiles.IsEnabled = !acting;
        Rescan.IsEnabled = false;
        CancelScan.Visibility = Visibility.Visible;
        Progress.Visibility = Visibility.Visible; Headline.Text = "Checking for apps…";
        UpdateSelectionHeader(ownGeneration);
        Status.Text = "Checking running apps…";
        try
        {
            var result = await scanner.ScanAsync(files.ToArray(), new ScanProgressReporter(DispatcherQueue, p =>
            { if (ownGeneration == generation && busy) Status.Text = p.Total > 0 ? $"Checking running apps ({p.Completed:N0} of {p.Total:N0})…" : $"Checking running apps ({p.Completed:N0})…"; }), ownCancellation.Token);
            if (ownGeneration != generation) return;
            var rows = await Task.Run(() => result.Processes.Select(p => new ProcessRow(controller.Inspect(p))).ToArray());
            if (ownGeneration != generation) return;
            ProcessList.ItemsSource = rows;
            foreach (var row in rows) _ = row.LoadIconAsync();
            // Require explicit selection for force ending; no scan implicitly chooses targets.
            SelectAll.IsChecked = false;
            ResultsCard.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (rows.Length > 0) GrowForReport(rows.Length);
            EmptyState.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            bool complete = result.Complete && unsupported == 0;
            lastScanComplete = complete;
            Headline.Text = rows.Length > 0 ? $"{rows.Length} {(rows.Length == 1 ? "app is" : "apps are")} using the selected items" :
                complete ? afterAction ? "No detected app remains" : "No apps found" : "Scan incomplete";
            EmptyTitle.Text = Headline.Text;
            Headline.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            NormalClose.Visibility = ForceEnd.Visibility = rows.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            Done.Visibility = startedFromLanding ? Visibility.Visible : Visibility.Collapsed;

            EmptyCheck.Visibility = complete ? Visibility.Visible : Visibility.Collapsed;
            EmptyIcon.Visibility = complete ? Visibility.Collapsed : Visibility.Visible;
            EmptyDescription.Text = complete ? result.Issues.Any(i => i.CoverageNote) ? "No app was detected using the files inside this folder. A program using the folder itself may still prevent moving or deleting it." : "No app was detected. If the file still won’t delete, permissions or a protected location may be the cause." :
                "Some files couldn't be checked. See below for details, or try scanning again.";
            string itemWord = result.Covered == 1 ? (files.Count > 0 && Directory.Exists(files.First()) ? "folder" : "file") : "items";
            Status.Text = $"{result.Covered} {itemWord} checked · {result.Elapsed.TotalMilliseconds:N0} ms" + (result.Cancelled ? " · Cancelled" : "") +
                (unsupported > 0 ? $" · {unsupported} unsupported items skipped" : "");
            Coverage.Visibility = complete ? Visibility.Collapsed : Visibility.Visible;
            Coverage.Header = "What couldn't be checked";
            if (complete && result.Issues.Any(i => i.CoverageNote))
                Status.Text += " · Apps using the folder itself may not be detected.";
            CoverageText.Text = "Apps shown may be using any of the selected files. A file may still be in use even if no apps are found.\n" +
                string.Join("\n", result.Issues.Take(100).Select(i => $"{i.Resource}: {i.Reason}")) +
                (result.Issues.Count > 100 ? $"\n{result.Issues.Count - 100} more errors omitted." : "");
            if (Environment.GetEnvironmentVariable("LOCKPEEK_TEST_REPORT") is { Length: > 0 } report)
                await File.WriteAllTextAsync(report, System.Text.Json.JsonSerializer.Serialize(new DiagnosticsReport(
                    files.ToArray(), unsupported, result, Environment.ProcessId,
                    System.Diagnostics.Process.GetCurrentProcess().WorkingSet64, WindowNative.GetWindowHandle(this).ToInt64()),
                    DiagnosticsJsonContext.Default.DiagnosticsReport));
        }
        catch (Exception e)
        {
            if (ownGeneration == generation)
            {
                Headline.Text = EmptyTitle.Text = "Scan could not finish";
                EmptyDescription.Text = e.Message;
                Headline.Visibility = ResultsCard.Visibility = NormalClose.Visibility = ForceEnd.Visibility = Visibility.Collapsed;
                EmptyState.Visibility = EmptyIcon.Visibility = Visibility.Visible;
                EmptyCheck.Visibility = Visibility.Collapsed;
                Done.Visibility = startedFromLanding ? Visibility.Visible : Visibility.Collapsed;
                Status.Text = "Try Rescan after checking that the selected items are still available.";
            }
        }
        finally
        {
            ownCancellation.Dispose();
            if (ownGeneration == generation)
            { scanCancellation = null; busy = false; Progress.Visibility = Visibility.Collapsed; CancelScan.Visibility = Visibility.Collapsed; Rescan.IsEnabled = files.Count > 0; UpdateActions(); }
        }
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSelection) return;
        updatingSelection = true;
        foreach (var row in e.AddedItems.OfType<ProcessRow>().Where(r => !IsEligible(r)))
            ProcessList.SelectedItems.Remove(row);
        var eligible = ProcessList.Items.OfType<ProcessRow>().Where(IsEligible).ToArray();
        SelectAll.IsChecked = eligible.Length > 0 && eligible.All(row => ProcessList.SelectedItems.Contains(row));
        updatingSelection = false;
        UpdateActions();
    }
    private static bool IsEligible(ProcessRow row) => row.Target.Eligibility.CanClose || row.Target.Eligibility.CanForceEnd;
    private void UpdateActions()
    {
        if (NormalClose is null) return;
        bool reporting = OutcomeView.Visibility == Visibility.Visible;
        StartScan.Visibility = landingActive ? Visibility.Visible : Visibility.Collapsed;
        Status.Visibility = reporting ? Visibility.Collapsed : Visibility.Visible;
        ReportStatus.Visibility = reporting ? Visibility.Visible : Visibility.Collapsed;
        SelectionHeader.Visibility = reporting || landingActive ? Visibility.Collapsed : Visibility.Visible;
        RecycleHint.Visibility = !reporting && deleteIntent ? Visibility.Visible : Visibility.Collapsed;
        SupportButton.Visibility = reporting ? Visibility.Collapsed : Visibility.Visible;
        if (reporting) SupportThanks.Visibility = Visibility.Collapsed;
        Rescan.Visibility = reporting || landingActive || ResultsCard.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        RescanInList.IsEnabled = !busy && !acting && files.Count > 0;
        var selected = ProcessList.SelectedItems.OfType<ProcessRow>().ToArray();
        NormalClose.IsEnabled = !busy && !acting && selected.Any(p => p.Target.Eligibility.CanClose);
        ForceEnd.IsEnabled = !busy && !acting && selected.Any(p => p.Target.Eligibility.CanForceEnd);
        NormalClose.Content = deleteIntent ? "Close & delete" : "Close normally";
        Recycle.Content = "Review & delete";
        Recycle.IsEnabled = deleteIntent && !busy && !acting && lastScanComplete && files.Count > 0 && unsupported == 0 && ProcessList.Items.Count == 0;
        Recycle.Visibility = Recycle.IsEnabled && OutcomeView.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(Recycle, Recycle.IsEnabled ? $"Review {files.Count} selected {(files.Count == 1 ? "item" : "items")} before deleting. Folders include their contents." : "Complete the scan and close detected apps first. You can also retry the operation in Explorer.");
        SelectAll.IsEnabled = !busy && !acting && ProcessList.Items.OfType<ProcessRow>().Any(IsEligible);
        ProcessList.IsEnabled = !busy && !acting;
        int forceCount = selected.Count(p => p.Target.Eligibility.CanForceEnd);
        ForceEnd.Content = deleteIntent ? "Force end & delete" : forceCount > 0 ? $"Force end ({forceCount})" : "Force end selected";
        ToolTipService.SetToolTip(ForceEnd, "Stops selected eligible apps immediately. Unsaved work may be lost. Protected apps are excluded.");
    }
    private void SelectEligible(object sender, RoutedEventArgs e)
    {
        updatingSelection = true;
        ProcessList.SelectedItems.Clear();
        if (SelectAll.IsChecked == true)
            foreach (var row in ProcessList.Items.OfType<ProcessRow>().Where(IsEligible)) ProcessList.SelectedItems.Add(row);
        updatingSelection = false;
        UpdateActions();
    }
    private void CopyDetails(object sender, RoutedEventArgs e)
    {
        var row = (ProcessRow)((Button)sender).Tag;
        var data = new DataPackage();
        data.SetText($"{row.Name}\n{row.ProcessId}\n{row.Executable}\n{row.IdentityDetails}\n{row.Reason}");
        Clipboard.SetContent(data);
    }
    private async void OpenLocation(object sender, RoutedEventArgs e)
    {
        var row = (ProcessRow)((Button)sender).Tag;
        try
        {
            if (row.Target.Evidence.Verified && Path.GetDirectoryName(row.Executable) is { } directory)
                await global::Windows.System.Launcher.LaunchFolderPathAsync(directory);
        }
        catch (Exception error) { Status.Text = "Could not open location: " + error.Message; }
    }
    private async void SupportClick(object sender, RoutedEventArgs e)
    { await global::Windows.System.Launcher.LaunchUriAsync(new Uri("https://ko-fi.com/darkorbitweaver")); }
    private async void ShowThanks()
    {
        int current = ++thanksGeneration;
        SupportThanks.Visibility = Visibility.Visible;
        SupportThanks.Opacity = 1;
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (current != thanksGeneration) return;
        if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            SupportThanks.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(400)) };
        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard { FillBehavior = Microsoft.UI.Xaml.Media.Animation.FillBehavior.Stop };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, SupportThanks);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => { if (current == thanksGeneration) SupportThanks.Visibility = Visibility.Collapsed; };
        storyboard.Begin();
    }
    private async Task<bool> ConfirmActionAsync(ProcessAction action, ProcessTarget[] eligible, IReadOnlyList<ProcessTarget> review)
    {
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = action == ProcessAction.ForceEnd
                ? "Force ending stops these apps immediately. Anything unsaved in them will be lost."
                : "Some selected apps cannot be closed automatically. Only eligible regular apps will receive a close request.",
            TextWrapping = TextWrapping.Wrap
        });
        foreach (var target in eligible.Concat(review))
        {
            var details = new StackPanel { Spacing = 4 };
            details.Children.Add(new TextBlock { Text = Path.GetFileName(target.Evidence.Executable), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            details.Children.Add(new TextBlock
            {
                Text = eligible.Contains(target) ? "May have unsaved work" : "Excluded: " + target.Eligibility.Reason,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new Border { Background = Root.Background, CornerRadius = new CornerRadius(4), Padding = new Thickness(12), Child = details });
        }
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = review.Count > 0 ? "Some apps need extra care" : "Force end selected apps?",
            Content = new ScrollViewer { MaxHeight = 320, Content = content },
            PrimaryButtonText = action == ProcessAction.ForceEnd ? "Force end selected" : "Close regular apps only",
            IsPrimaryButtonEnabled = eligible.Length > 0,
            SecondaryButtonText = review.Count > 0 ? "Review selection" : "",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private async Task ActAsync(ProcessAction action)
    {
        if (acting || busy) return;
        var selectedRows = ProcessList.SelectedItems.OfType<ProcessRow>().ToArray();
        var selected = selectedRows.Select(r => r.Target).ToArray();
        if (selected.Length == 0) return;
        bool offerDelete = false;
        acting = true;
        AddFiles.IsEnabled = false;
        Rescan.IsEnabled = false;
        UpdateActions();
        try
        {
            // Re-inspect before constructing the review, then again using the action handle immediately before acting.
            var fresh = await Task.Run(() => selected.Select(t => controller.Inspect(t.Detection)).ToArray());
            var review = SafetyPolicy.Review(fresh, action);
            var eligible = fresh.Where(t => action == ProcessAction.ForceEnd ? t.Eligibility.CanForceEnd : t.Eligibility.CanClose).ToArray();
            if ((action == ProcessAction.ForceEnd || review.Count > 0) && !await ConfirmActionAsync(action, eligible, review)) return;
            var outcomes = await new ActionWorkflow(controller).ExecuteAsync(fresh, action,
                forceConfirmed: action == ProcessAction.ForceEnd, CancellationToken.None);
            await ScanAsync(true);
            offerDelete = deleteIntent && pending is null && lastScanComplete && ProcessList.Items.Count == 0 && files.Count > 0;
            ResultsCard.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            OutcomeView.Visibility = Visibility.Visible;
            Headline.Visibility = Visibility.Visible;
            int ended = outcomes.Count(o => o.Exited);
            if (ended > 0 && !offerDelete && pending is null) ShowThanks();
            Headline.Text = $"{ended} of {outcomes.Count} {(outcomes.Count == 1 ? "app" : "apps")} ended";
            allDeletionOutcomes = null;
            outcomeSearchGeneration++;
            OutcomeSearch.Visibility = Visibility.Collapsed;
            ShowAllOutcomes.Visibility = Visibility.Collapsed;
            OutcomeRows.Visibility = Visibility.Visible;
            SetOutcomeRows(outcomes.Select(outcome => new OutcomeRow(outcome) { SourceRow = selectedRows.FirstOrDefault(r => r.Target.Detection.Identity == outcome.Identity) }).ToArray());
            GrowForReport(outcomes.Count);
            NormalClose.Visibility = ForceEnd.Visibility = Visibility.Collapsed;
            Done.Visibility = Visibility.Visible;
            int unclosedCount = ProcessList.Items.Count - outcomes.Count(o => o.Exited);
            ReviewRemaining.Visibility = unclosedCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (deleteIntent && !offerDelete)
                Status.Text = "Deletion was not started. Review the remaining apps or scan coverage, then try again.";
        }
        catch (Exception e) { Status.Text = "Action could not finish: " + e.Message; }
        finally
        {
            acting = false;
            AddFiles.IsEnabled = true;
            Rescan.IsEnabled = files.Count > 0;
            UpdateActions();
            if (pending is { } selection) { offerDelete = false; pending = null; Receive(selection); }
        }
        if (offerDelete) await DeleteAsync();
    }
    private async void RecycleClick(object sender, RoutedEventArgs e) => await DeleteAsync();
    private async Task DeleteAsync()
    {
        if (!deleteIntent || busy || acting || !Recycle.IsEnabled) return;
        acting = true;
        AddFiles.IsEnabled = Rescan.IsEnabled = false;
        UpdateActions();
        using var cancellation = new CancellationTokenSource();
        deleteReviewCancellation = cancellation;
        CancelScan.Visibility = Visibility.Visible;
        Status.Text = "Checking the selected items…";
        try
        {
            // Review only original selected items, never the expanded scan-resource list.
            var targets = await Task.Run(() => RecyclePolicy.Prepare(files, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            CancelScan.Visibility = Visibility.Collapsed;
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = "Review the items below. Folders include their contents as they exist when Windows deletes them. Windows uses the Recycle Bin when possible and may warn before permanent deletion.", TextWrapping = TextWrapping.Wrap });
            if (targets.Length < files.Count)
                content.Children.Add(new TextBlock { Text = "Items inside another selected folder are included with that folder.", FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var itemList = new ListView { ItemsSource = targets.Select(t => $"{((t.Attributes & FileAttributes.Directory) != 0 ? "Folder" : "File")} · {Path.GetFileName(t.Path)}  —  {t.Path}").ToArray(), MaxHeight = 200, SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = false };
            ScrollViewer.SetHorizontalScrollMode(itemList, ScrollMode.Enabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(itemList, ScrollBarVisibility.Auto);
            content.Children.Add(itemList);
            string title = targets.Length == 1
                ? (targets[0].Attributes & FileAttributes.Directory) != 0 ? "Delete this folder?" : "Delete this file?"
                : $"Delete {targets.Length} items?";
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = content, PrimaryButtonText = "Delete", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            CancelScan.Visibility = Visibility.Visible;
            Status.Text = "Checking the selected items again…";
            var results = await RecycleBin.MoveAsync(targets, WindowNative.GetWindowHandle(this), cancellation.Token,
                () => DispatcherQueue.TryEnqueue(() => CancelScan.Visibility = Visibility.Collapsed));
            foreach (var result in results.Where(r => r.Deleted))
                files.RemoveWhere(path => RecyclePolicy.Within(path, result.Path));
            ResultsCard.Visibility = EmptyState.Visibility = Visibility.Collapsed;
            OutcomeView.Visibility = Visibility.Visible;
            int deleted = results.Count(r => r.Deleted);
            Headline.Visibility = Visibility.Visible;
            Headline.Text = deleted == results.Count ? $"{deleted} of {results.Count} items deleted" : $"{deleted} of {results.Count} items confirmed deleted";
            var targetByPath = targets.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
            allDeletionOutcomes = results.Select(r => new OutcomeRow(Path.GetFileName(r.Path), r.Recycled || r.Message == "Deletion was not confirmed" ? "" : r.Message, r.Recycled ? "In Recycle Bin" : r.Deleted ? "Deleted" : "Not confirmed", r.Deleted)
            {
                ItemGlyph = (targetByPath[r.Path].Attributes & FileAttributes.Directory) != 0 ? "\uE8B7" : "\uE8A5",
                ItemPath = r.Path
            }).ToArray();
            bool summarize = allDeletionOutcomes.Length > 5;
            outcomeSearchGeneration++;
            OutcomeSearch.Visibility = Visibility.Collapsed;
            OutcomeSearch.Text = "";
            var preview = summarize ? allDeletionOutcomes.Where(row => !row.Success).Concat(allDeletionOutcomes.Where(row => row.Success)).Take(5).ToArray() : allDeletionOutcomes;
            SetOutcomeRows(preview);
            GrowForReport(preview.Length);
            OutcomeRows.Visibility = Visibility.Visible;
            ShowAllOutcomes.Content = $"Show {allDeletionOutcomes.Length - preview.Length} more";
            ShowAllOutcomes.Visibility = summarize ? Visibility.Visible : Visibility.Collapsed;
            Status.Text = "Items moved to the Recycle Bin can be restored there. Check the results for other items.";
            NormalClose.Visibility = ForceEnd.Visibility = ReviewRemaining.Visibility = Visibility.Collapsed;
            Done.Visibility = Visibility.Visible;
            lastScanComplete = false;
            UpdateSelectionHeader(generation);
            if (deleted > 0 && pending is null) ShowThanks();
        }
        catch (OperationCanceledException) { Status.Text = "Delete cancelled. No items were deleted."; }
        catch (Exception error) { Status.Text = "Delete could not finish. Check the selected items: " + error.Message; }
        finally
        {
            deleteReviewCancellation = null;
            CancelScan.Visibility = Visibility.Collapsed;
            acting = false;
            AddFiles.IsEnabled = true;
            Rescan.IsEnabled = files.Count > 0;
            UpdateActions();
            if (pending is { } selection) { pending = null; Receive(selection); }
        }
    }
    private void ReviewRemainingClick(object sender, RoutedEventArgs e)
    {
        OutcomeView.Visibility = ReviewRemaining.Visibility = Done.Visibility = Visibility.Collapsed;
        ResultsCard.Visibility = NormalClose.Visibility = ForceEnd.Visibility = Visibility.Visible;
        Headline.Text = $"{ProcessList.Items.Count} {(ProcessList.Items.Count == 1 ? "app remains" : "apps remain")} detected";
        UpdateActions();
    }
    private void ShowAllOutcomesClick(object sender, RoutedEventArgs e)
    {
        if (allDeletionOutcomes is null) return;
        SetOutcomeRows(allDeletionOutcomes);
        OutcomeRows.Visibility = Visibility.Visible;
        OutcomeSearch.Visibility = Visibility.Visible;
        GrowForReport(allDeletionOutcomes.Length, showSearch: true);
        ShowAllOutcomes.Visibility = Visibility.Collapsed;
    }
    private async void OutcomeSearchChanged(object sender, TextChangedEventArgs e)
    {
        int current = ++outcomeSearchGeneration;
        var items = allDeletionOutcomes;
        if (items is null || OutcomeSearch.Visibility != Visibility.Visible) return;
        string query = OutcomeSearch.Text;
        await Task.Delay(250);
        if (current != outcomeSearchGeneration) return;
        var matches = await Task.Run(() => items.Where(row =>
            row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (row.ItemPath?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToArray());
        if (current == outcomeSearchGeneration && ReferenceEquals(items, allDeletionOutcomes)) SetOutcomeRows(matches);
    }
    private void SetOutcomeRows(OutcomeRow[] rows)
    {
        if (firstOutcome is not null) firstOutcome.IsFirst = false;
        firstOutcome = rows.FirstOrDefault();
        if (firstOutcome is not null) firstOutcome.IsFirst = true;
        OutcomeRows.ItemsSource = rows;
    }
    private async void CloseClick(object sender, RoutedEventArgs e) => await ActAsync(ProcessAction.CloseNormally);
    private async void ForceClick(object sender, RoutedEventArgs e) => await ActAsync(ProcessAction.ForceEnd);
    private async void RescanClick(object sender, RoutedEventArgs e) => await ScanAsync();
    private async void DoneClick(object sender, RoutedEventArgs e)
    {
        if (files.Count == 0 || (startedFromLanding && OutcomeView.Visibility != Visibility.Visible))
        {
            ShowLanding();
        }
        else if (OutcomeView.Visibility == Visibility.Visible)
        {
            if (startedFromLanding)
            {
                ShowLanding();
            }
            else
            {
                Close();
            }
        }
        else
        {
            await ScanAsync(true);
        }
    }
    private void CancelClick(object sender, RoutedEventArgs e)
    {
        scanCancellation?.Cancel();
        deleteReviewCancellation?.Cancel();
    }

    private sealed class ScanProgressReporter(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, Action<ScanProgress> update) : IProgress<ScanProgress>
    {
        private long lastUpdate;
        private int queued;
        private ScanProgress? latest;

        public void Report(ScanProgress progress)
        {
            latest = progress;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastUpdate != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastUpdate, now) < TimeSpan.FromMilliseconds(40)) return;
            if (Interlocked.CompareExchange(ref queued, 1, 0) != 0) return;
            lastUpdate = now;
            if (!dispatcher.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref queued, 0);
                var current = latest;
                if (current is not null) update(current);
            })) Interlocked.Exchange(ref queued, 0);
        }
    }
}
