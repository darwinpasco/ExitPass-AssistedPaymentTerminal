using System.IO;
using System.Windows;

namespace AssistedPaymentTerminal.Desktop;

public partial class App : Application
{
    private DesktopSingleInstanceLease? _singleInstanceLease;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = StartupOptions.FromEnvironmentAndArgs(e.Args);
        var validation = ProfileValidator.Validate(options.Profile);

        if (!validation.IsValid)
        {
            if (options.SmokeCheckOnly)
            {
                Shutdown(2);
                return;
            }

            MessageBox.Show(
                validation.Message,
                "ExitPass Assisted Payment Terminal startup refused",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        try
        {
            var source = WebViewSourceResolver.Resolve(options);
            if (options.SmokeCheckOnly)
            {
                Shutdown(0);
                return;
            }

            if (options.WebViewSmokeCheck)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                _ = Dispatcher.BeginInvoke(new Action(async () =>
                {
                    var result = await WebViewRenderSmokeRunner.RunAsync(source, TimeSpan.FromSeconds(30));
                    if (!result.Succeeded)
                    {
                        System.Diagnostics.Trace.TraceError(
                            "WebView render smoke failed. reason={0} url={1}",
                            result.ErrorMessage,
                            source.NavigationUri);
                    }

                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "exitpass-apt-webview-smoke-result.txt"),
                        result.Succeeded ? "PASSED" : $"FAILED: {result.ErrorMessage}");

                    Environment.Exit(result.Succeeded ? 0 : 4);
                }));
                return;
            }

            _singleInstanceLease = DesktopSingleInstanceLease.TryAcquire(options.TerminalId);
            if (_singleInstanceLease is null)
            {
                MessageBox.Show(
                    "This terminal application is already running. Use the existing window.",
                    "ExitPass Assisted Payment Terminal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown(5);
                return;
            }

            MainWindow = new MainWindow(source, options);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Unable to load the terminal shell. {exception.Message}",
                "ExitPass Assisted Payment Terminal startup failure",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceLease?.Dispose();
        _singleInstanceLease = null;
        base.OnExit(e);
    }
}
