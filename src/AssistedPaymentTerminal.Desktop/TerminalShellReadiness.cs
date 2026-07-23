namespace AssistedPaymentTerminal.Desktop;

public static class TerminalShellReadiness
{
    public const string DataTestId = "apt-terminal-shell";
    public const string ReadySelectorForScript = "[data-testid=\"apt-terminal-shell\"][data-app-ready=\"true\"]";
    public const string ReadySelectorForDiagnostics = "[data-testid='apt-terminal-shell'][data-app-ready='true']";
    public const string MissingMountMessage = "The page loaded, but the terminal shell did not mount.";

    public static string BuildMountFailureDetail(string errorReference, string? url, string? startupError)
    {
        var detail = $"Reference: {errorReference}\nURL: {url}\nExpected marker: {ReadySelectorForDiagnostics}";

        if (!string.IsNullOrWhiteSpace(startupError))
        {
            detail += $"\nStartup error: {startupError}";
        }

        return detail;
    }
}
