using System.Reflection;
using System.Text.Json;

namespace AssistedPaymentTerminal.LocalOperations;

public sealed record CashCustodyDenominationDefinition(
    string Code,
    string DisplayLabel,
    int ValueMinorUnits);

public static class CashCustodyDenominationCatalog
{
    private const string ResourceName = "AssistedPaymentTerminal.LocalOperations.CashCustodyDenominations.json";

    public static IReadOnlyList<CashCustodyDenominationDefinition> Supported { get; } = Load();

    public static IReadOnlyList<CashCustodyDenominationLine> CreateCount(
        IReadOnlyDictionary<string, int>? quantities = null) =>
        Supported.Select(denomination => new CashCustodyDenominationLine(
            denomination.Code,
            denomination.ValueMinorUnits,
            quantities?.GetValueOrDefault(denomination.Code) ?? 0)).ToArray();

    private static IReadOnlyList<CashCustodyDenominationDefinition> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The authoritative cash-custody denomination catalogue is unavailable.");
        var definitions = JsonSerializer.Deserialize<CashCustodyDenominationDefinition[]>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The authoritative cash-custody denomination catalogue is invalid.");

        if (definitions.Length == 0
            || definitions.Any(value => string.IsNullOrWhiteSpace(value.Code)
                || string.IsNullOrWhiteSpace(value.DisplayLabel)
                || value.ValueMinorUnits <= 0)
            || definitions.Select(value => value.Code).Distinct(StringComparer.Ordinal).Count() != definitions.Length
            || definitions.Select(value => value.ValueMinorUnits).Distinct().Count() != definitions.Length)
        {
            throw new InvalidOperationException("The authoritative cash-custody denomination catalogue is invalid.");
        }

        return Array.AsReadOnly(definitions);
    }
}
