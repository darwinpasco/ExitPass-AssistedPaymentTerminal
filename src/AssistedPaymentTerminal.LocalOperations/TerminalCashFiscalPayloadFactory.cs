using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssistedPaymentTerminal.LocalOperations;

public static class TerminalCashFiscalPayloadFactory
{
    public static TerminalCashFiscalIssuanceRequest CreateRequest() => new();

    public static string Serialize(TerminalCashFiscalIssuanceRequest request) =>
        JsonSerializer.Serialize(request, TerminalCashPaymentPayloadFactory.JsonOptions);

    public static string ComputeHash(string requestRepresentation)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(requestRepresentation));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
