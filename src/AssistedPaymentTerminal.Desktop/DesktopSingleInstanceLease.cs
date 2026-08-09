using System.Security.Cryptography;
using System.Text;

namespace AssistedPaymentTerminal.Desktop;

public sealed class DesktopSingleInstanceLease : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _ownsLease;

    private DesktopSingleInstanceLease(Semaphore semaphore)
    {
        _semaphore = semaphore;
        _ownsLease = true;
    }

    public static DesktopSingleInstanceLease? TryAcquire(string? terminalId)
    {
        var identity = string.IsNullOrWhiteSpace(terminalId) ? "unconfigured" : terminalId.Trim();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var semaphore = new Semaphore(1, 1, $"Local\\ExitPass.APT.{digest}");
        return semaphore.WaitOne(0)
            ? new DesktopSingleInstanceLease(semaphore)
            : DisposeAndReturnNull(semaphore);
    }

    public void Dispose()
    {
        if (_ownsLease)
        {
            _ownsLease = false;
            _semaphore.Release();
        }
        _semaphore.Dispose();
    }

    private static DesktopSingleInstanceLease? DisposeAndReturnNull(Semaphore semaphore)
    {
        semaphore.Dispose();
        return null;
    }
}
