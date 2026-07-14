namespace AssistedPaymentTerminal.Desktop;

public sealed record StartupOptions(
    string? Profile,
    string? DevelopmentWebUiUrl,
    string BaseDirectory,
    bool PreferPackagedAssets,
    bool SmokeCheckOnly,
    bool WebViewSmokeCheck = false)
{
    public static StartupOptions FromEnvironmentAndArgs(string[] args)
    {
        var profile = Environment.GetEnvironmentVariable("APT_PROFILE");
        var webUiUrl = Environment.GetEnvironmentVariable("APT_WEB_UI_URL");
        var preferPackagedAssets = false;
        var smokeCheckOnly = false;
        var webViewSmokeCheck = false;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            {
                profile = arg["--profile=".Length..];
            }
            else if (arg.StartsWith("--web-ui-url=", StringComparison.OrdinalIgnoreCase))
            {
                webUiUrl = arg["--web-ui-url=".Length..];
            }
            else if (arg.Equals("--packaged-assets", StringComparison.OrdinalIgnoreCase))
            {
                preferPackagedAssets = true;
            }
            else if (arg.Equals("--smoke-check", StringComparison.OrdinalIgnoreCase))
            {
                smokeCheckOnly = true;
            }
            else if (arg.Equals("--webview-smoke-check", StringComparison.OrdinalIgnoreCase))
            {
                smokeCheckOnly = false;
                webViewSmokeCheck = true;
            }
        }

        return new StartupOptions(profile, webUiUrl, AppContext.BaseDirectory, preferPackagedAssets, smokeCheckOnly, webViewSmokeCheck);
    }
}
