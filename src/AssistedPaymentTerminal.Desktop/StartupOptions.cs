namespace AssistedPaymentTerminal.Desktop;

public sealed record StartupOptions(
    string? Profile,
    string? DevelopmentWebUiUrl,
    string BaseDirectory,
    bool PreferPackagedAssets,
    bool SmokeCheckOnly,
    bool WebViewSmokeCheck = false,
    bool EnableNonLiveCashCapture = false,
    string? LocalDatabasePath = null,
    bool EnableCentralPmsCashSubmission = false,
    bool EnableCentralPmsFiscalIssuance = false,
    bool EnableCentralPmsReceiptRetrieval = false,
    string? CentralPmsBaseUrl = null)
{
    public static StartupOptions FromEnvironmentAndArgs(string[] args)
    {
        var profile = Environment.GetEnvironmentVariable("APT_PROFILE");
        var webUiUrl = Environment.GetEnvironmentVariable("APT_WEB_UI_URL");
        var preferPackagedAssets = false;
        var smokeCheckOnly = false;
        var webViewSmokeCheck = false;
        var enableNonLiveCashCapture = IsTrue(Environment.GetEnvironmentVariable("APT_ENABLE_NON_LIVE_CASH_CAPTURE"));
        var localDatabasePath = Environment.GetEnvironmentVariable("APT_LOCAL_DB_PATH");
        var enableCentralPmsCashSubmission = IsTrue(Environment.GetEnvironmentVariable("APT_ENABLE_CENTRAL_PMS_CASH_SUBMISSION"));
        var enableCentralPmsFiscalIssuance = IsTrue(Environment.GetEnvironmentVariable("APT_ENABLE_CENTRAL_PMS_FISCAL_ISSUANCE"));
        var enableCentralPmsReceiptRetrieval = IsTrue(Environment.GetEnvironmentVariable("APT_ENABLE_CENTRAL_PMS_RECEIPT_RETRIEVAL"));
        var centralPmsBaseUrl = Environment.GetEnvironmentVariable("CENTRAL_PMS_BASE_URL");

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
            else if (arg.Equals("--enable-non-live-cash-capture", StringComparison.OrdinalIgnoreCase))
            {
                enableNonLiveCashCapture = true;
            }
            else if (arg.StartsWith("--local-db-path=", StringComparison.OrdinalIgnoreCase))
            {
                localDatabasePath = arg["--local-db-path=".Length..];
            }
            else if (arg.Equals("--enable-central-pms-cash-submission", StringComparison.OrdinalIgnoreCase))
            {
                enableCentralPmsCashSubmission = true;
            }
            else if (arg.Equals("--enable-central-pms-fiscal-issuance", StringComparison.OrdinalIgnoreCase))
            {
                enableCentralPmsFiscalIssuance = true;
            }
            else if (arg.Equals("--enable-central-pms-receipt-retrieval", StringComparison.OrdinalIgnoreCase))
            {
                enableCentralPmsReceiptRetrieval = true;
            }
            else if (arg.StartsWith("--central-pms-base-url=", StringComparison.OrdinalIgnoreCase))
            {
                centralPmsBaseUrl = arg["--central-pms-base-url=".Length..];
            }
        }

        return new StartupOptions(
            profile,
            webUiUrl,
            AppContext.BaseDirectory,
            preferPackagedAssets,
            smokeCheckOnly,
            webViewSmokeCheck,
            enableNonLiveCashCapture,
            string.IsNullOrWhiteSpace(localDatabasePath) ? null : localDatabasePath,
            enableCentralPmsCashSubmission,
            enableCentralPmsFiscalIssuance,
            enableCentralPmsReceiptRetrieval,
            string.IsNullOrWhiteSpace(centralPmsBaseUrl) ? null : centralPmsBaseUrl);
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
