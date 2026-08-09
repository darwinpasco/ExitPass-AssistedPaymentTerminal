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
    bool EnableReceiptPreview = false,
    bool EnableReceiptPrinting = false,
    string? ReceiptPaperWidthMm = null,
    string? ReceiptPrinterName = null,
    string? ReceiptPrinterMode = null,
    string? SiteTimeZoneId = null,
    string? CentralPmsBaseUrl = null,
    string? ManualProofDiagnosticPath = null,
    string? CentralPmsServiceIdentityId = null,
    string? TerminalId = null,
    string? SiteId = null,
    string? SiteGroupId = null,
    string? PosServerId = null,
    string? HumanSessionCredentialPath = null)
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
        var enableReceiptPreview = IsTrue(Environment.GetEnvironmentVariable("APT_ENABLE_RECEIPT_PREVIEW"));
        var enableReceiptPrinting = IsTrue(Environment.GetEnvironmentVariable("APT_ENABLE_RECEIPT_PRINTING"));
        var receiptPaperWidthMm = Environment.GetEnvironmentVariable("APT_RECEIPT_PAPER_WIDTH_MM");
        var receiptPrinterName = Environment.GetEnvironmentVariable("APT_RECEIPT_PRINTER_NAME");
        var receiptPrinterMode = Environment.GetEnvironmentVariable("APT_RECEIPT_PRINTER_MODE");
        var siteTimeZoneId = Environment.GetEnvironmentVariable("APT_SITE_TIME_ZONE_ID");
        var centralPmsBaseUrl = Environment.GetEnvironmentVariable("CENTRAL_PMS_BASE_URL");
        var manualProofDiagnosticPath = Environment.GetEnvironmentVariable("APT_MANUAL_PROOF_DIAGNOSTIC_PATH");
        var centralPmsServiceIdentityId = Environment.GetEnvironmentVariable("APT_CENTRAL_PMS_SERVICE_IDENTITY_ID");
        var terminalId = Environment.GetEnvironmentVariable("APT_TERMINAL_ID");
        var siteId = Environment.GetEnvironmentVariable("APT_SITE_ID");
        var siteGroupId = Environment.GetEnvironmentVariable("APT_SITE_GROUP_ID");
        var posServerId = Environment.GetEnvironmentVariable("APT_POS_SERVER_ID");
        var humanSessionCredentialPath = Environment.GetEnvironmentVariable("APT_HUMAN_SESSION_CREDENTIAL_PATH");

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
            else if (arg.Equals("--enable-receipt-preview", StringComparison.OrdinalIgnoreCase))
            {
                enableReceiptPreview = true;
            }
            else if (arg.Equals("--enable-receipt-printing", StringComparison.OrdinalIgnoreCase))
            {
                enableReceiptPrinting = true;
            }
            else if (arg.StartsWith("--receipt-paper-width-mm=", StringComparison.OrdinalIgnoreCase))
            {
                receiptPaperWidthMm = arg["--receipt-paper-width-mm=".Length..];
            }
            else if (arg.StartsWith("--receipt-printer-name=", StringComparison.OrdinalIgnoreCase))
            {
                receiptPrinterName = arg["--receipt-printer-name=".Length..];
            }
            else if (arg.StartsWith("--receipt-printer-mode=", StringComparison.OrdinalIgnoreCase))
            {
                receiptPrinterMode = arg["--receipt-printer-mode=".Length..];
            }
            else if (arg.StartsWith("--site-time-zone-id=", StringComparison.OrdinalIgnoreCase))
            {
                siteTimeZoneId = arg["--site-time-zone-id=".Length..];
            }
            else if (arg.StartsWith("--central-pms-base-url=", StringComparison.OrdinalIgnoreCase))
            {
                centralPmsBaseUrl = arg["--central-pms-base-url=".Length..];
            }
            else if (arg.StartsWith("--manual-proof-diagnostic-path=", StringComparison.OrdinalIgnoreCase))
            {
                manualProofDiagnosticPath = arg["--manual-proof-diagnostic-path=".Length..];
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
            enableReceiptPreview,
            enableReceiptPrinting,
            string.IsNullOrWhiteSpace(receiptPaperWidthMm) ? null : receiptPaperWidthMm,
            string.IsNullOrWhiteSpace(receiptPrinterName) ? null : receiptPrinterName,
            string.IsNullOrWhiteSpace(receiptPrinterMode) ? null : receiptPrinterMode,
            string.IsNullOrWhiteSpace(siteTimeZoneId) ? null : siteTimeZoneId,
            string.IsNullOrWhiteSpace(centralPmsBaseUrl) ? null : centralPmsBaseUrl,
            string.IsNullOrWhiteSpace(manualProofDiagnosticPath) ? null : manualProofDiagnosticPath,
            string.IsNullOrWhiteSpace(centralPmsServiceIdentityId) ? null : centralPmsServiceIdentityId,
            string.IsNullOrWhiteSpace(terminalId) ? null : terminalId,
            string.IsNullOrWhiteSpace(siteId) ? null : siteId,
            string.IsNullOrWhiteSpace(siteGroupId) ? null : siteGroupId,
            string.IsNullOrWhiteSpace(posServerId) ? null : posServerId,
            string.IsNullOrWhiteSpace(humanSessionCredentialPath) ? null : humanSessionCredentialPath);
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
