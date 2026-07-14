using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace AssistedPaymentTerminal.Desktop;

public sealed record WebViewRenderSmokeResult(bool Succeeded, string? ErrorMessage);

public static class WebViewRenderSmokeRunner
{
    public static async Task<WebViewRenderSmokeResult> RunAsync(WebViewSource source, TimeSpan timeout)
    {
        var completion = new TaskCompletionSource<WebViewRenderSmokeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceFailures = new List<string>();
        var consoleErrors = new List<string>();

        var window = new Window
        {
            Title = "ExitPass APT WebView Smoke",
            Width = 1024,
            Height = 768,
            ShowInTaskbar = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var webView = new WebView2();
        window.Content = webView;

        using var cancellation = new CancellationTokenSource(timeout);
        window.Show();
        window.Activate();
        await Task.Delay(250);

        try
        {
            await webView.EnsureCoreWebView2Async();
            var core = webView.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 initialization completed without CoreWebView2.");

            if (source.IsPackaged)
            {
                if (string.IsNullOrWhiteSpace(source.VirtualHostName) ||
                    string.IsNullOrWhiteSpace(source.PackagedAssetsDirectory))
                {
                    throw new InvalidOperationException("Packaged WebView source is missing virtual-host mapping details.");
                }

                core.SetVirtualHostNameToFolderMapping(
                    source.VirtualHostName,
                    source.PackagedAssetsDirectory,
                    CoreWebView2HostResourceAccessKind.Allow);
            }

            core.Settings.AreDevToolsEnabled = false;
            core.WebMessageReceived += (_, args) =>
            {
                var message = args.TryGetWebMessageAsString();
                Trace.TraceInformation(
                    "WebView smoke frontend diagnostic. message={0}",
                    message);

                if (message.Contains("\"level\":\"error\"", StringComparison.OrdinalIgnoreCase))
                {
                    consoleErrors.Add(message);
                }
            };
            core.ProcessFailed += (_, args) =>
            {
                completion.TrySetResult(new WebViewRenderSmokeResult(
                    false,
                    $"WebView2 process failed: {args.ProcessFailedKind}."));
            };
            core.WebResourceResponseReceived += (_, args) =>
            {
                var statusCode = args.Response?.StatusCode ?? 0;
                if (statusCode >= 400 && !args.Request.Uri.EndsWith("/favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    resourceFailures.Add($"{statusCode} {args.Request.Uri}");
                }
            };
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess || args.HttpStatusCode >= 400)
                {
                    completion.TrySetResult(new WebViewRenderSmokeResult(
                        false,
                        $"Navigation failed. success={args.IsSuccess} status={args.HttpStatusCode} error={args.WebErrorStatus}."));
                    return;
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

            core.Navigate(source.NavigationUri.ToString());

            var readyTask = WaitForReadyDomAsync(webView, cancellation.Token);
            var completedTask = await Task.WhenAny(completion.Task, readyTask);
            if (completedTask == completion.Task)
            {
                return await completion.Task;
            }

            if (!await readyTask)
            {
                var details = resourceFailures.Count > 0
                    ? $" Resource failures: {string.Join("; ", resourceFailures)}."
                    : string.Empty;
                var console = consoleErrors.Count > 0
                    ? $" Console errors: {string.Join("; ", consoleErrors)}."
                    : string.Empty;

                return new WebViewRenderSmokeResult(
                    false,
                    $"Timed out waiting for WebView2 readiness marker at {source.NavigationUri}.{details}{console}");
            }

            if (resourceFailures.Count > 0)
            {
                return new WebViewRenderSmokeResult(
                    false,
                    $"Required assets failed: {string.Join("; ", resourceFailures)}.");
            }

            if (consoleErrors.Count > 0)
            {
                return new WebViewRenderSmokeResult(
                    false,
                    $"Browser console errors occurred: {string.Join("; ", consoleErrors)}.");
            }

            return new WebViewRenderSmokeResult(true, null);
        }
        catch (Exception exception)
        {
            return new WebViewRenderSmokeResult(false, exception.Message);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<bool> WaitForReadyDomAsync(WebView2 webView, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var marker = await webView.ExecuteScriptAsync(
                "Boolean(document.querySelector('[data-testid=\"apt-mode1-shell\"][data-app-ready=\"true\"]'))");
            var heading = await webView.ExecuteScriptAsync(
                "Boolean(document.body && document.body.innerText && document.body.innerText.includes('Cashier-Assisted Terminal') && document.body.innerText.includes('Mode 1'))");

            if (ReadBooleanScriptResult(marker) && ReadBooleanScriptResult(heading))
            {
                return true;
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private static async Task<bool> ExecuteBooleanScriptAsync(WebView2 webView, string script)
    {
        var result = await webView.ExecuteScriptAsync(script);
        return ReadBooleanScriptResult(result);
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
}
