using System.IO;
using System.Text;
using System.Text.Json;

namespace AssistedPaymentTerminal.Desktop;

public sealed class HumanAuthenticationTrace
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _sync = new();
    private readonly string? _path;

    public HumanAuthenticationTrace(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
        HostInstanceReference = Guid.NewGuid();
        Record("host.created", sourceMethod: nameof(HumanAuthenticationTrace));
    }

    public Guid HostInstanceReference { get; }

    public static HumanAuthenticationTrace FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("APT_HUMAN_AUTHENTICATION_TRACE_PATH"));

    public void Record(
        string eventName,
        string? operation = null,
        string? sourceMethod = null,
        string? sourceTrigger = null,
        bool? explicitUserAction = null,
        Guid? attemptReference = null,
        string? hostCorrelationId = null,
        Guid? centralPmsCorrelationId = null,
        string? outcome = null)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            var entry = new
            {
                timestamp = DateTimeOffset.UtcNow,
                hostInstanceReference = HostInstanceReference,
                processId = Environment.ProcessId,
                managedThreadId = Environment.CurrentManagedThreadId,
                eventName,
                operation,
                sourceMethod,
                sourceTrigger,
                explicitUserAction,
                attemptReference,
                hostCorrelationId,
                centralPmsCorrelationId,
                outcome
            };
            var line = JsonSerializer.Serialize(entry, JsonOptions);
            lock (_sync)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.AppendAllText(_path, line + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Optional diagnostics must never affect authentication behavior.
        }
    }
}
