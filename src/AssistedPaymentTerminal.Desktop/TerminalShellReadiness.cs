namespace AssistedPaymentTerminal.Desktop;

public static class TerminalShellReadiness
{
    public const string HumanLoginDataTestId = "apt-human-login-shell";
    public const string TerminalDataTestId = "apt-terminal-shell";
    public const string HumanLoginReadySelectorForScript = "[data-testid=\"apt-human-login-shell\"][data-app-ready=\"true\"]";
    public const string TerminalReadySelectorForScript = "[data-testid=\"apt-terminal-shell\"][data-app-ready=\"true\"]";
    public const string ReadySelectorForScript = HumanLoginReadySelectorForScript + ", " + TerminalReadySelectorForScript;
    public const string HumanLoginReadySelectorForDiagnostics = "[data-testid='apt-human-login-shell'][data-app-ready='true']";
    public const string TerminalReadySelectorForDiagnostics = "[data-testid='apt-terminal-shell'][data-app-ready='true']";
    public const string MissingMountMessage = "The page loaded, but neither initialized application shell mounted.";

    public static string BuildMountFailureDetail(string errorReference, string? url, string? startupError)
    {
        var detail = $"Reference: {errorReference}\nURL: {url}\nExpected pre-authentication marker: {HumanLoginReadySelectorForDiagnostics}\nExpected authenticated marker: {TerminalReadySelectorForDiagnostics}";

        if (!string.IsNullOrWhiteSpace(startupError))
        {
            detail += $"\nStartup error: {startupError}";
        }

        return detail;
    }
}
