using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public interface IGovernedPhysicalTerminalSerialReader
{
    string Read();
}

public sealed class WindowsCimPhysicalTerminalSerialReader : IGovernedPhysicalTerminalSerialReader
{
    public string Read()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Physical APT terminal verification requires Windows CIM.");

        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powershellPath = Path.Combine(
            windowsDirectory,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        if (!File.Exists(powershellPath))
            throw new InvalidOperationException("The governed Windows CIM host is unavailable.");

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
            "$ErrorActionPreference='Stop'; [Console]::Out.Write([string](Get-CimInstance -ClassName Win32_BIOS -ErrorAction Stop).SerialNumber)");

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The governed Windows CIM host could not be started.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("The physical terminal serial query timed out.");
            }

            Task.WaitAll(output, error);
            var serial = output.Result.Trim();
            if (process.ExitCode != 0 ||
                string.IsNullOrWhiteSpace(serial) ||
                serial.Length > 128 ||
                serial.Contains('\r', StringComparison.Ordinal) ||
                serial.Contains('\n', StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Windows CIM did not return one valid physical terminal serial.");
            }
            return serial;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The physical terminal serial could not be read through Windows CIM.", exception);
        }
    }
}

public sealed record GovernedDeviceEnrollmentResultEvidence(
    bool Ok,
    GovernedDeviceEnrollmentOutcome Enrollment,
    bool ProvisioningPackageDeleted,
    bool ReconciliationRequired,
    bool ProvisioningCredentialReplayProhibited);

public interface IGovernedDeviceEnrollmentResultReservation : IDisposable
{
    void SetPotentialCommit(bool potentialCommit);
    void Write(GovernedDeviceEnrollmentResultEvidence evidence);
}

public interface IGovernedDeviceEnrollmentResultStore
{
    IGovernedDeviceEnrollmentResultReservation Reserve(string path);
}

public sealed class FileGovernedDeviceEnrollmentResultStore : IGovernedDeviceEnrollmentResultStore
{
    public IGovernedDeviceEnrollmentResultReservation Reserve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new GovernedDeviceEnrollmentException("APT_ENROLLMENT_RESULT_DESTINATION_UNAVAILABLE");

        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var stream = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough);
            stream.Flush(flushToDisk: true);
            return new FileGovernedDeviceEnrollmentResultReservation(fullPath, stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new GovernedDeviceEnrollmentException(
                "APT_ENROLLMENT_RESULT_DESTINATION_UNAVAILABLE",
                innerException: exception);
        }
    }

    private sealed class FileGovernedDeviceEnrollmentResultReservation(
        string path,
        FileStream stream) : IGovernedDeviceEnrollmentResultReservation
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        private bool _potentialCommit;
        private bool _completed;

        public void SetPotentialCommit(bool potentialCommit) => _potentialCommit = potentialCommit;

        public void Write(GovernedDeviceEnrollmentResultEvidence evidence)
        {
            ArgumentNullException.ThrowIfNull(evidence);
            stream.SetLength(0);
            JsonSerializer.Serialize(stream, evidence, JsonOptions);
            stream.Flush(flushToDisk: true);
            _completed = true;
        }

        public void Dispose()
        {
            stream.Dispose();
            if (_completed || _potentialCommit) return;
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A harmless empty preflight marker may remain; it prevents accidental replay.
            }
        }
    }
}
