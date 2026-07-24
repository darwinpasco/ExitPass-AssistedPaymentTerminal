using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace AssistedPaymentTerminal.Desktop;

public partial class MainWindow : Window
{
    private readonly WebViewSource _source;
    private readonly StartupOptions _options;
    private readonly LocalJournalBridgeHandler _localJournalBridge;
    private bool _eventsRegistered;

    public MainWindow(WebViewSource source, StartupOptions options)
    {
        _source = source;
        _options = options;
        var localOptions = new AssistedPaymentTerminal.LocalOperations.LocalOperationsDatabaseOptions(
            options.LocalDatabasePath,
            CentralPmsBaseUrl: options.CentralPmsBaseUrl ?? "UNCONFIGURED_CENTRAL_PMS",
            EnableCentralPmsCashSubmission: options.EnableCentralPmsCashSubmission,
            EnableCentralPmsFiscalIssuance: options.EnableCentralPmsFiscalIssuance,
            EnableCentralPmsReceiptRetrieval: options.EnableCentralPmsReceiptRetrieval);
        var journal = new AssistedPaymentTerminal.LocalOperations.CashJournalService(localOptions);
        IReceiptPrinter receiptPrinter = options.ReceiptPrinterMode?.Trim().ToLowerInvariant() switch
        {
            "controlled" => new ControlledReceiptPrinter(),
            "visual-smoke" => new VisualSmokeReceiptPrinter(),
            _ => new WindowsReceiptPrinter()
        };
        _localJournalBridge = new LocalJournalBridgeHandler(
            journal,
            options.EnableNonLiveCashCapture,
            options.EnableCentralPmsCashSubmission,
            options.EnableCentralPmsFiscalIssuance,
            options.EnableCentralPmsReceiptRetrieval,
            options.EnableReceiptPreview,
            options.ReceiptPaperWidthMm,
            options.CentralPmsBaseUrl,
            new AssistedPaymentTerminal.LocalOperations.TerminalCashPaymentSubmissionService(
                new AssistedPaymentTerminal.LocalOperations.CentralPmsTerminalCashPaymentClient(new HttpClient()),
                localOptions),
            new AssistedPaymentTerminal.LocalOperations.TerminalCashFiscalSubmissionService(
                new AssistedPaymentTerminal.LocalOperations.CentralPmsTerminalCashFiscalClient(new HttpClient()),
                localOptions),
            new AssistedPaymentTerminal.LocalOperations.TerminalCashReceiptRetrievalService(
                new AssistedPaymentTerminal.LocalOperations.CentralPmsTerminalCashReceiptClient(new HttpClient()),
                localOptions),
            receiptPrintingEnabled: options.EnableReceiptPrinting,
            receiptPrinterName: options.ReceiptPrinterName,
            receiptPrinter: receiptPrinter,
            siteTimeZoneId: options.SiteTimeZoneId);
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

            var readiness = await WaitForReadinessMarkerAsync(TimeSpan.FromSeconds(12));
            if (!readiness.Ready)
            {
                var errorReference = CreateErrorReference();
                ShowError(
                    "Terminal interface did not start",
                    TerminalShellReadiness.MissingMountMessage,
                    TerminalShellReadiness.BuildMountFailureDetail(errorReference, TerminalWebView.Source?.ToString(), readiness.StartupError));
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

        core.WebMessageReceived += async (_, args) =>
        {
            var message = args.TryGetWebMessageAsString();
            var response = await _localJournalBridge.HandleWebMessageAsync(message);

            if (response is not null)
            {
                core.PostWebMessageAsString(response);
                return;
            }

            Trace.TraceInformation("WebView2 frontend diagnostic. message={0}", message);
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
              window.__APT_STARTUP_ERROR__ = "";
              const rememberStartupError = (message, detail) => {
                window.__APT_STARTUP_ERROR__ = `${String(message ?? "")} ${String(detail ?? "")}`.trim().slice(0, 500);
              };
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
              window.__APT_DESKTOP_FLAGS__ = {
                APT_ENABLE_NON_LIVE_CASH_CAPTURE: "__APT_ENABLE_NON_LIVE_CASH_CAPTURE__",
                APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION: "__APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION__",
                APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE: "__APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE__",
                APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL: "__APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL__",
                APT_ENABLE_RECEIPT_PREVIEW: "__APT_ENABLE_RECEIPT_PREVIEW__",
                APT_ENABLE_RECEIPT_PRINTING: "__APT_ENABLE_RECEIPT_PRINTING__",
                APT_RECEIPT_PAPER_WIDTH_MM: "__APT_RECEIPT_PAPER_WIDTH_MM__",
                APT_RECEIPT_PRINTER_NAME: "__APT_RECEIPT_PRINTER_NAME__",
                CENTRAL_PMS_BASE_URL: "__CENTRAL_PMS_BASE_URL__"
              };
              const originalError = console.error.bind(console);
              console.error = (...args) => {
                post("error", args.map(String).join(" "), "console.error");
                originalError(...args);
              };
              window.addEventListener("error", event => {
                rememberStartupError(event.message, `${event.filename}:${event.lineno}:${event.colno}`);
                post("error", event.message, `${event.filename}:${event.lineno}:${event.colno}`);
              });
              window.addEventListener("unhandledrejection", event => {
                const reason = event.reason?.message ?? event.reason;
                rememberStartupError(reason, "unhandledrejection");
                post("error", reason, "unhandledrejection");
              });
            })();
            """.Replace(
                "__APT_ENABLE_NON_LIVE_CASH_CAPTURE__",
                _options.EnableNonLiveCashCapture ? "true" : "false")
            .Replace(
                "__APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION__",
                _options.EnableCentralPmsCashSubmission ? "true" : "false")
            .Replace(
                "__APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE__",
                _options.EnableCentralPmsFiscalIssuance ? "true" : "false")
            .Replace(
                "__APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL__",
                _options.EnableCentralPmsReceiptRetrieval ? "true" : "false")
            .Replace(
                "__APT_ENABLE_RECEIPT_PREVIEW__",
                _options.EnableReceiptPreview ? "true" : "false")
            .Replace(
                "__APT_ENABLE_RECEIPT_PRINTING__",
                _options.EnableReceiptPrinting ? "true" : "false")
            .Replace(
                "__APT_RECEIPT_PAPER_WIDTH_MM__",
                JavaScriptStringEncode(_options.ReceiptPaperWidthMm ?? ""))
            .Replace(
                "__APT_RECEIPT_PRINTER_NAME__",
                JavaScriptStringEncode(_options.ReceiptPrinterName ?? ""))
            .Replace(
                "__CENTRAL_PMS_BASE_URL__",
                JavaScriptStringEncode(_options.CentralPmsBaseUrl ?? "")));
    }

    private static string JavaScriptStringEncode(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private async Task<ReadinessMarkerResult> WaitForReadinessMarkerAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        string? startupError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await ReadinessMarkerScriptResultAsync();
            startupError = result.StartupError;

            if (result.Ready)
            {
                return result;
            }

            await Task.Delay(250);
        }

        return new ReadinessMarkerResult(false, startupError);
    }

    private async Task<ReadinessMarkerResult> ReadinessMarkerScriptResultAsync()
    {
        try
        {
            var json = await TerminalWebView.ExecuteScriptAsync(
                """
                (() => ({
                  ready: Boolean(document.querySelector('__APT_TERMINAL_READY_SELECTOR__')),
                  startupError: String(window.__APT_STARTUP_ERROR__ || "")
                }))()
                """.Replace("__APT_TERMINAL_READY_SELECTOR__", TerminalShellReadiness.ReadySelectorForScript));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var ready = root.TryGetProperty("ready", out var readyElement) && readyElement.GetBoolean();
            var startupError = root.TryGetProperty("startupError", out var startupElement)
                ? startupElement.GetString()
                : null;

            return new ReadinessMarkerResult(ready, startupError);
        }
        catch (JsonException)
        {
            return new ReadinessMarkerResult(false, "Unable to read terminal shell readiness marker.");
        }
        catch (InvalidOperationException)
        {
            return new ReadinessMarkerResult(false, "Unable to read terminal shell readiness marker.");
        }
    }

    private sealed record ReadinessMarkerResult(bool Ready, string? StartupError);

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
