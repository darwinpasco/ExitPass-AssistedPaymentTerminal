using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace AssistedPaymentTerminal.Desktop;

public partial class MainWindow : Window
{
    private readonly WebViewSource _source;
    private readonly StartupOptions _options;
    private bool _eventsRegistered;

    public MainWindow(WebViewSource source, StartupOptions options)
    {
        _source = source;
        _options = options;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await StartWebViewAsync();
    }

    private async Task StartWebViewAsync()
    {
        var errorReference = CreateErrorReference();

        try
        {
            ShowLoading(
                "Starting terminal",
                "Loading the cashier terminal...",
                $"Source: {_source.SafeDisplayLocation}");

            Trace.TraceInformation(
                "Starting ExitPass Assisted Payment Terminal. profile={0} url={1} source={2}",
                _options.Profile,
                _source.NavigationUri,
                _source.SafeDisplayLocation);

            await TerminalWebView.EnsureCoreWebView2Async();
            await ConfigureWebViewAsync();
            TerminalWebView.CoreWebView2.Navigate(_source.NavigationUri.ToString());
        }
        catch (Exception exception)
        {
            Trace.TraceError(
                "WebView startup failed. reference={0} url={1} source={2} message={3}",
                errorReference,
                _source.NavigationUri,
                _source.SafeDisplayLocation,
                exception);

            ShowError(
                "Terminal startup failed",
                "WebView2 could not load the cashier terminal.",
                $"Reference: {errorReference}\nSource: {_source.SafeDisplayLocation}\nURL: {_source.NavigationUri}\nReason: {exception.Message}");
        }
    }

    private async Task ConfigureWebViewAsync()
    {
        var core = TerminalWebView.CoreWebView2;
        if (core is null)
        {
            throw new InvalidOperationException("WebView2 initialization completed without CoreWebView2.");
        }

        if (_source.IsPackaged)
        {
            if (string.IsNullOrWhiteSpace(_source.VirtualHostName) ||
                string.IsNullOrWhiteSpace(_source.PackagedAssetsDirectory))
            {
                throw new InvalidOperationException("Packaged WebView source is missing virtual-host mapping details.");
            }

            core.SetVirtualHostNameToFolderMapping(
                _source.VirtualHostName,
                _source.PackagedAssetsDirectory,
                CoreWebView2HostResourceAccessKind.Allow);
        }

        core.Settings.AreDevToolsEnabled = !_source.IsPackaged;
        core.Settings.IsStatusBarEnabled = false;

        if (_eventsRegistered)
        {
            return;
        }

        _eventsRegistered = true;
        core.NavigationStarting += (_, args) =>
        {
            ShowLoading("Loading terminal", "Waiting for the cashier terminal interface...", $"URL: {args.Uri}");
            Trace.TraceInformation("WebView2 navigation starting. uri={0}", args.Uri);
        };

        core.NavigationCompleted += async (_, args) =>
        {
            Trace.TraceInformation(
                "WebView2 navigation completed. success={0} status={1} error={2} url={3}",
                args.IsSuccess,
                args.HttpStatusCode,
                args.WebErrorStatus,
                TerminalWebView.Source);

            if (!args.IsSuccess || args.HttpStatusCode >= 400)
            {
                var errorReference = CreateErrorReference();
                ShowError(
                    "Terminal navigation failed",
                    "The cashier terminal did not load successfully.",
                    $"Reference: {errorReference}\nURL: {TerminalWebView.Source}\nHTTP status: {args.HttpStatusCode}\nWebView2 status: {args.WebErrorStatus}");
                return;
            }

            var ready = await WaitForReadinessMarkerAsync(TimeSpan.FromSeconds(12));
            if (!ready)
            {
                var errorReference = CreateErrorReference();
                ShowError(
                    "Terminal interface did not start",
                    "The page loaded, but the React Mode 1 terminal did not mount.",
                    $"Reference: {errorReference}\nURL: {TerminalWebView.Source}\nExpected marker: [data-testid='apt-mode1-shell'][data-app-ready='true']");
                return;
            }

            StatusOverlay.Visibility = Visibility.Collapsed;
            ErrorActions.Visibility = Visibility.Collapsed;
            TerminalWebView.Visibility = Visibility.Visible;
        };

        core.ProcessFailed += (_, args) =>
        {
            var errorReference = CreateErrorReference();
            Trace.TraceError(
                "WebView2 process failed. reference={0} kind={1} reason={2}",
                errorReference,
                args.ProcessFailedKind,
                args.Reason);
            ShowError(
                "Terminal browser process failed",
                "The embedded browser process stopped unexpectedly.",
                $"Reference: {errorReference}\nFailure kind: {args.ProcessFailedKind}\nReason: {args.Reason}");
        };

        core.WebMessageReceived += (_, args) =>
        {
            var message = args.TryGetWebMessageAsString();
            Trace.TraceInformation(
                "WebView2 frontend diagnostic. message={0}",
                message);
        };

        core.WebResourceResponseReceived += (_, args) =>
        {
            var statusCode = args.Response?.StatusCode ?? 0;
            if (statusCode >= 400)
            {
                Trace.TraceWarning(
                    "WebView2 resource failed. status={0} uri={1}",
                    statusCode,
                    args.Request.Uri);
            }
        };

        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            """
            (() => {
              const post = (level, message, detail) => {
                try {
                  window.chrome?.webview?.postMessage(JSON.stringify({
                    source: "apt-frontend-diagnostic",
                    level,
                    message: String(message ?? ""),
                    detail: String(detail ?? "")
                  }));
                } catch {
                  // Diagnostic reporting must never block terminal startup.
                }
              };
              const originalError = console.error.bind(console);
              console.error = (...args) => {
                post("error", args.map(String).join(" "), "console.error");
                originalError(...args);
              };
              window.addEventListener("error", event => {
                post("error", event.message, `${event.filename}:${event.lineno}:${event.colno}`);
              });
              window.addEventListener("unhandledrejection", event => {
                post("error", event.reason?.message ?? event.reason, "unhandledrejection");
              });
            })();
            """);
    }

    private async Task<bool> WaitForReadinessMarkerAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var marker = await TerminalWebView.ExecuteScriptAsync(
                "Boolean(document.querySelector('[data-testid=\"apt-mode1-shell\"][data-app-ready=\"true\"]'))");
            var heading = await TerminalWebView.ExecuteScriptAsync(
                "Boolean(document.body && document.body.innerText && document.body.innerText.includes('Cashier-Assisted Terminal'))");

            if (ReadBooleanScriptResult(marker) && ReadBooleanScriptResult(heading))
            {
                return true;
            }

            await Task.Delay(250);
        }

        return false;
    }

    private static bool ReadBooleanScriptResult(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<bool>(json);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ShowLoading(string title, string message, string detail)
    {
        TerminalWebView.Visibility = Visibility.Collapsed;
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        StatusDetail.Text = detail;
        ErrorActions.Visibility = Visibility.Collapsed;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void ShowError(string title, string message, string detail)
    {
        TerminalWebView.Visibility = Visibility.Collapsed;
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        StatusDetail.Text = detail;
        ErrorActions.Visibility = Visibility.Visible;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await StartWebViewAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string CreateErrorReference() =>
        $"APT-WV2-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
}
