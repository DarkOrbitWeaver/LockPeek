using Microsoft.UI.Xaml;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace LockPeek.App;

public partial class App : Application
{
    private MainWindow? window;
    private Mutex? instance;
    private readonly CancellationTokenSource lifetime = new();

    public App()
    {
        UnhandledException += (_, error) =>
        {
            RecordStartupFailure(error.Exception);
            error.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                RecordStartupFailure(ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            RecordStartupFailure(e.Exception);
            e.SetObserved();
        };

        try
        {
            InitializeComponent();
        }
        catch (Exception error)
        {
            RecordStartupFailure(error);
            throw;
        }
    }

    private static void RecordStartupFailure(Exception error)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockPeek");
            Directory.CreateDirectory(directory);
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(Path.Combine(directory, "crash.log"), $"[{timestamp} UTC] {error}\n\n");
            File.WriteAllText(Path.Combine(directory, "startup-error.txt"), error.ToString());
        }
        catch
        {
            // Suppress logging errors to avoid recursive faults
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        instance = new Mutex(true, @"Local\" + SelectionTransport.PipeName, out bool first);
        if (!first)
        {
            try
            {
                await SelectionTransport.SendAsync(await SelectionTransport.ReadLaunchAsync());
            }
            catch (Exception error)
            {
                RecordStartupFailure(error);
                ShowLaunchError(IntPtr.Zero, "LockPeek could not pass this selection to its existing window. Close LockPeek and try again.", "LockPeek", 0x10);
            }
            Exit();
            return;
        }

        window = new MainWindow();
        window.Closed += (_, _) =>
        {
            lifetime.Cancel();
            instance.ReleaseMutex();
            instance.Dispose();
            Exit();
        };

        window.Activate();
        _ = ListenAsync();

        try
        {
            window.Receive(await SelectionTransport.ReadLaunchAsync());
        }
        catch (Exception e)
        {
            window.ReportLaunchError(e.Message);
        }
    }

    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int ShowLaunchError(IntPtr owner, string message, string title, uint type);

    private async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    SelectionTransport.PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(lifetime.Token);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));

                var selection = await SelectionTransport.ReadAsync(pipe, timeout.Token);
                window?.DispatcherQueue.TryEnqueue(() =>
                {
                    window.ShowFromExplorer();
                    window.Receive(selection);
                });
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                window?.DispatcherQueue.TryEnqueue(() => window.ReportLaunchError(e.Message));
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
