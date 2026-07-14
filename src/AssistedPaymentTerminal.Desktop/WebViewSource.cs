namespace AssistedPaymentTerminal.Desktop;

public sealed record WebViewSource(
    Uri NavigationUri,
    bool IsPackaged,
    string? PackagedAssetsDirectory,
    string? VirtualHostName,
    string SafeDisplayLocation)
{
    public const string PackagedHostName = "apt.local";
}
