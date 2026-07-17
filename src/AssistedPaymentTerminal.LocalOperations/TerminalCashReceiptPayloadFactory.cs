using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssistedPaymentTerminal.LocalOperations;

public static class TerminalCashReceiptPayloadFactory
{
    public const string HashSourceVersion = "terminal-cash-receipt-presentation:sha256:v1";

    public static string Serialize(JsonElement authoritativePresentation) =>
        JsonSerializer.Serialize(authoritativePresentation, TerminalCashPaymentPayloadFactory.JsonOptions);

    public static string ComputeHash(string authoritativePresentationJson)
    {
        var source = $"{HashSourceVersion}:{authoritativePresentationJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
