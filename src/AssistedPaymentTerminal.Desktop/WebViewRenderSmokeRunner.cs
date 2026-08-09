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
        var webMessages = new List<string>();

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
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            await core.Profile.ClearBrowsingDataAsync(
                CoreWebView2BrowsingDataKinds.PasswordAutosave |
                CoreWebView2BrowsingDataKinds.GeneralAutofill);
            core.WebMessageReceived += (_, args) =>
            {
                var message = args.TryGetWebMessageAsString();
                webMessages.Add(message);
                TryRespondToHumanSessionRestore(core, message);
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

            var credentialBoundaryFailure = await VerifyBrowserCredentialExclusionAsync(webView, webMessages);
            if (credentialBoundaryFailure is not null)
            {
                return new WebViewRenderSmokeResult(false, credentialBoundaryFailure);
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

    private static async Task<string?> VerifyBrowserCredentialExclusionAsync(WebView2 webView, List<string> webMessages)
    {
        var hasLoginShell = await ExecuteBooleanScriptAsync(
            webView,
            "Boolean(document.querySelector('[data-testid=\"apt-human-login-shell\"][data-app-ready=\"true\"]'))");
        if (!hasLoginShell)
        {
            return "The WebView credential-exclusion proof requires the initialized human-login shell.";
        }

        var browserOwnsPasswordInput = await ExecuteBooleanScriptAsync(
            webView,
            "Boolean(document.querySelector('input[type=\"password\"]'))");
        if (browserOwnsPasswordInput)
        {
            return "The initialized human-login shell exposed a browser-controlled password input.";
        }

        var submitted = await ExecuteBooleanScriptAsync(
            webView,
            """
            (() => {
              const shell = document.querySelector('[data-testid="apt-human-login-shell"]');
              const form = shell?.querySelector('form');
              const username = shell?.querySelector('#cashierUsername');
              if (!form || !username) return false;
              username.value = 'cashier.webview-smoke';
              const rogueCredentialInput = document.createElement('input');
              rogueCredentialInput.type = 'password';
              rogueCredentialInput.value = 'prohibited-browser-restored-value';
              rogueCredentialInput.hidden = true;
              form.appendChild(rogueCredentialInput);
              form.requestSubmit();
              rogueCredentialInput.remove();
              return true;
            })()
            """);
        if (!submitted)
        {
            return "The initialized human-login shell could not execute the WebView credential-exclusion proof.";
        }

        await Task.Delay(250);
        var loginMessages = webMessages.Where(message =>
            message.Contains("\"source\":\"apt-human-session\"", StringComparison.Ordinal)
            && message.Contains("\"command\":\"humanSession.login\"", StringComparison.Ordinal)).ToArray();
        if (loginMessages.Length != 1)
        {
            return $"Expected exactly one username-only login prompt request, observed {loginMessages.Length}.";
        }

        using var document = JsonDocument.Parse(loginMessages[0]);
        var payload = document.RootElement.GetProperty("payload");
        var properties = payload.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != 1
            || !string.Equals(properties[0], "username", StringComparison.Ordinal)
            || loginMessages[0].Contains("prohibited-browser-restored-value", StringComparison.Ordinal))
        {
            return "The WebView login request exposed browser-controlled credential data.";
        }

        return null;
    }

    private static void TryRespondToHumanSessionRestore(CoreWebView2 core, string message)
    {
        try
        {
            using var request = JsonDocument.Parse(message);
            var root = request.RootElement;
            if (!root.TryGetProperty("source", out var source)
                || !string.Equals(source.GetString(), HumanSessionBridgeCommand.Source, StringComparison.Ordinal)
                || !root.TryGetProperty("command", out var command)
                || !string.Equals(command.GetString(), HumanSessionBridgeCommand.Restore, StringComparison.Ordinal)
                || !root.TryGetProperty("correlationId", out var correlationId))
            {
                return;
            }

            core.PostWebMessageAsString(JsonSerializer.Serialize(new
            {
                source = HumanSessionBridgeCommand.Source,
                ok = true,
                command = HumanSessionBridgeCommand.Restore,
                correlationId = correlationId.GetString(),
                payload = new
                {
                    authenticationState = "UNAUTHENTICATED",
                    authenticated = false,
                    deviceTrusted = true,
                    shiftOperationsAuthorized = false,
                    custodyOperationsAuthorized = false,
                    cashOperationsAuthorized = false,
                    userReference = (string?)null,
                    username = (string?)null,
                    displayName = (string?)null,
                    audience = (string?)null,
                    assurance = (string?)null,
                    privilegedAccount = false,
                    mfaRequired = false,
                    idleExpiresAt = (string?)null,
                    absoluteExpiresAt = (string?)null,
                    safeSupportReference = "APT-WEBVIEW-SMOKE",
                    safeMessage = "Cashier sign-in is required.",
                    errorCode = (string?)null,
                    retryable = false,
                    activeShift = (object?)null,
                    activeCashCustodySession = (object?)null
                }
            }));
        }
        catch (JsonException)
        {
            // Non-human-session diagnostics are irrelevant to this bounded smoke response.
        }
    }

    private static async Task<bool> WaitForReadyDomAsync(WebView2 webView, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var marker = await webView.ExecuteScriptAsync(
                $"Boolean(document.querySelector('{TerminalShellReadiness.ReadySelectorForScript}'))");

            if (ReadBooleanScriptResult(marker))
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
