using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.Desktop.Tests;

internal static class TestCashCustodyDenominations
{
    public static IReadOnlyCollection<CashCustodyDenominationLine> ForAmount(decimal amount)
    {
        var minorUnits = decimal.ToInt64(amount * 100m);
        var result = new List<CashCustodyDenominationLine>(CashCustodyDenominationCatalog.Supported.Count);
        foreach (var denomination in CashCustodyDenominationCatalog.Supported)
        {
            var quantity = checked((int)(minorUnits / denomination.ValueMinorUnits));
            minorUnits %= denomination.ValueMinorUnits;
            result.Add(new CashCustodyDenominationLine(denomination.Code, denomination.ValueMinorUnits, quantity));
        }

        if (minorUnits != 0)
        {
            throw new InvalidOperationException("The test amount cannot be represented by the custody denomination catalogue.");
        }

        return result;
    }
}
