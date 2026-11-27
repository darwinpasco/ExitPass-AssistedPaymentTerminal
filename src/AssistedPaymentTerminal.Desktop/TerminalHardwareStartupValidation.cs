using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

internal static class TerminalHardwareEnvironment
{
    public const string ExpectedBiosSerial = "APT_EXPECTED_PHYSICAL_BIOS_SERIAL";
    public const string ExpectedSsdSerial = "APT_TERMINAL_SERIAL_NUMBER";
    public const string PhysicalDiskUniqueId = "APT_PHYSICAL_DISK_UNIQUE_ID";
}

internal sealed record TerminalHardwareStartupExpectations(
    string? ExpectedBiosSerial,
    string? ExpectedSsdSerial,
    string? PhysicalDiskUniqueId)
{
    public static TerminalHardwareStartupExpectations FromEnvironment() => new(
        Environment.GetEnvironmentVariable(TerminalHardwareEnvironment.ExpectedBiosSerial),
        Environment.GetEnvironmentVariable(TerminalHardwareEnvironment.ExpectedSsdSerial),
        Environment.GetEnvironmentVariable(TerminalHardwareEnvironment.PhysicalDiskUniqueId));
}

internal sealed record WindowsPhysicalDiskIdentity(
    string? UniqueId,
    string? FriendlyName,
    string? SerialNumber);

internal interface IWindowsPhysicalDiskIdentityReader
{
    IReadOnlyList<WindowsPhysicalDiskIdentity> Read();
}

internal sealed class WindowsPowerShellPhysicalDiskIdentityReader : IWindowsPhysicalDiskIdentityReader
{
    private const int QueryTimeoutMilliseconds = 15_000;
    private const int MaximumOutputCharacters = 1_048_576;

    public IReadOnlyList<WindowsPhysicalDiskIdentity> Read()
    {
        if (!OperatingSystem.IsWindows())
            throw new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_QUERY_UNSUPPORTED",
                "Physical SSD verification requires Windows storage CIM.");

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershellPath = Path.Combine(
            windowsDirectory,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershellPath))
            throw new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_QUERY_UNAVAILABLE",
                "The governed Windows storage CIM host is unavailable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$ErrorActionPreference='Stop'; " +
            "$disks=@(Get-CimInstance -Namespace 'root/Microsoft/Windows/Storage' -ClassName MSFT_PhysicalDisk -ErrorAction Stop | " +
            "Select-Object UniqueId,FriendlyName,SerialNumber); " +
            "[Console]::Out.Write((ConvertTo-Json -InputObject $disks -Compress -Depth 3))");

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new TerminalHardwareStartupException(
                    "APT_PHYSICAL_DISK_QUERY_UNAVAILABLE",
                    "The governed Windows storage CIM host could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(QueryTimeoutMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                throw new TerminalHardwareStartupException(
                    "APT_PHYSICAL_DISK_QUERY_TIMEOUT",
                    "The physical SSD identity query timed out.");
            }

            Task.WaitAll(output, error);
            if (process.ExitCode != 0)
                throw new TerminalHardwareStartupException(
                    "APT_PHYSICAL_DISK_QUERY_FAILED",
                    "Windows storage CIM could not read the physical SSD identity.");
            if (output.Result.Length > MaximumOutputCharacters)
                throw new TerminalHardwareStartupException(
                    "APT_PHYSICAL_DISK_QUERY_INVALID",
                    "Windows storage CIM returned an unexpectedly large physical-disk inventory.");

            return Parse(output.Result);
        }
        catch (TerminalHardwareStartupException)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_QUERY_FAILED",
                "The physical SSD identity could not be read through Windows storage CIM.",
                exception);
        }
    }

    internal static IReadOnlyList<WindowsPhysicalDiskIdentity> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_QUERY_EMPTY",
                "Windows storage CIM returned no physical-disk inventory.");

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new TerminalHardwareStartupException(
                    "APT_PHYSICAL_DISK_QUERY_INVALID",
                    "Windows storage CIM returned an invalid physical-disk inventory.");

            var disks = new List<WindowsPhysicalDiskIdentity>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                disks.Add(new WindowsPhysicalDiskIdentity(
                    ReadString(item, "UniqueId"),
                    ReadString(item, "FriendlyName"),
                    ReadString(item, "SerialNumber")));
            }
            return disks;
        }
        catch (TerminalHardwareStartupException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_QUERY_INVALID",
                "Windows storage CIM returned an unreadable physical-disk inventory.",
                exception);
        }
    }

    private static string? ReadString(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

internal sealed record TerminalHardwareStartupEvidence(
    string BiosSerial,
    string PhysicalDiskUniqueId,
    string PhysicalDiskModel,
    string SsdSerial);

internal sealed class TerminalHardwareStartupException : Exception
{
    public TerminalHardwareStartupException(string code, string message, Exception? innerException = null)
        : base($"{code}: {message}", innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

internal interface ITerminalHardwareStartupGuard
{
    TerminalHardwareStartupEvidence Validate();
}

internal sealed class TerminalHardwareStartupGuard : ITerminalHardwareStartupGuard
{
    private readonly TerminalHardwareStartupExpectations _expectations;
    private readonly IGovernedPhysicalTerminalSerialReader _biosSerialReader;
    private readonly IWindowsPhysicalDiskIdentityReader _physicalDiskReader;

    public TerminalHardwareStartupGuard(
        TerminalHardwareStartupExpectations expectations,
        IGovernedPhysicalTerminalSerialReader? biosSerialReader = null,
        IWindowsPhysicalDiskIdentityReader? physicalDiskReader = null)
    {
        _expectations = expectations;
        _biosSerialReader = biosSerialReader ?? new WindowsCimPhysicalTerminalSerialReader();
        _physicalDiskReader = physicalDiskReader ?? new WindowsPowerShellPhysicalDiskIdentityReader();
    }

    public static TerminalHardwareStartupGuard FromEnvironment() =>
        new(TerminalHardwareStartupExpectations.FromEnvironment());

    public TerminalHardwareStartupEvidence Validate()
    {
        var expectedBios = RequireExpectation(
            _expectations.ExpectedBiosSerial,
            TerminalHardwareEnvironment.ExpectedBiosSerial);
        var expectedSsd = RequireExpectation(
            _expectations.ExpectedSsdSerial,
            TerminalHardwareEnvironment.ExpectedSsdSerial);
        var diskSelector = RequireExpectation(
            _expectations.PhysicalDiskUniqueId,
            TerminalHardwareEnvironment.PhysicalDiskUniqueId);

        string actualBios;
        try
        {
            actualBios = _biosSerialReader.Read().Trim();
        }
        catch (Exception exception) when (exception is not TerminalHardwareStartupException)
        {
            throw new TerminalHardwareStartupException(
                "APT_BIOS_SERIAL_QUERY_FAILED",
                "The physical BIOS serial could not be read.",
                exception);
        }

        if (!IsValidIdentity(actualBios))
            throw new TerminalHardwareStartupException(
                "APT_BIOS_SERIAL_UNREADABLE",
                "The physical BIOS serial is blank or invalid.");
        if (!string.Equals(actualBios, expectedBios, StringComparison.OrdinalIgnoreCase))
            throw new TerminalHardwareStartupException(
                "APT_BIOS_SERIAL_MISMATCH",
                $"Physical BIOS serial mismatch. Expected '{expectedBios}'; found '{actualBios}'.");

        IReadOnlyList<WindowsPhysicalDiskIdentity> disks;
        try
        {
            disks = _physicalDiskReader.Read();
        }
        catch (TerminalHardwareStartupException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_QUERY_FAILED",
                "The physical SSD identity could not be read.",
                exception);
        }

        var selected = disks
            .Where(disk => string.Equals(disk.UniqueId?.Trim(), diskSelector, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (selected.Length == 0)
            throw new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_NOT_FOUND",
                $"No physical SSD matched configured unique ID '{diskSelector}'.");
        if (selected.Length > 1)
            throw new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_AMBIGUOUS",
                $"Multiple physical SSDs matched configured unique ID '{diskSelector}'.");

        var actualSsd = NormalizePhysicalDiskSerial(selected[0].SerialNumber);
        if (!IsValidIdentity(actualSsd))
            throw new TerminalHardwareStartupException(
                "APT_SSD_SERIAL_UNREADABLE",
                "The selected physical SSD serial is blank or invalid.");
        if (!string.Equals(actualSsd, expectedSsd, StringComparison.OrdinalIgnoreCase))
            throw new TerminalHardwareStartupException(
                "APT_SSD_SERIAL_MISMATCH",
                $"Physical SSD serial mismatch. Expected '{expectedSsd}'; found '{actualSsd}'.");

        return new TerminalHardwareStartupEvidence(
            actualBios,
            diskSelector,
            selected[0].FriendlyName?.Trim() ?? "unknown model",
            actualSsd);
    }

    internal static string NormalizePhysicalDiskSerial(string? serial) =>
        (serial ?? string.Empty).Trim().TrimEnd('.');

    private static string RequireExpectation(string? value, string settingName)
    {
        var normalized = value?.Trim();
        if (!IsValidIdentity(normalized))
            throw new TerminalHardwareStartupException(
                "APT_HARDWARE_EXPECTATION_INVALID",
                $"Required setting {settingName} is missing or invalid.");
        return normalized!;
    }

    private static bool IsValidIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 &&
        !value.Contains('\r', StringComparison.Ordinal) &&
        !value.Contains('\n', StringComparison.Ordinal) &&
        !value.Any(char.IsControl);
}

internal static class DesktopStartupPreflight
{
    public static WebViewSource ValidateHardwareAndResolveSource(
        StartupOptions options,
        ITerminalHardwareStartupGuard? hardwareGuard = null,
        Func<StartupOptions, WebViewSource>? sourceResolver = null)
    {
        var evidence = (hardwareGuard ?? TerminalHardwareStartupGuard.FromEnvironment()).Validate();
        Trace.TraceInformation(
            "APT physical hardware startup validation passed. bios={0} diskUniqueId={1} diskModel={2} ssd={3}",
            evidence.BiosSerial,
            evidence.PhysicalDiskUniqueId,
            evidence.PhysicalDiskModel,
            evidence.SsdSerial);
        return (sourceResolver ?? WebViewSourceResolver.Resolve)(options);
    }
}
