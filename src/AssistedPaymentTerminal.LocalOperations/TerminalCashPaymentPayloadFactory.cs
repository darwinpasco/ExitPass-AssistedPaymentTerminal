using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AssistedPaymentTerminal.LocalOperations;

public static class TerminalCashPaymentPayloadFactory
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TerminalCashPaymentRequest CreatePayload(
        CashTender tender,
        CashCustodySession session,
        CashTenderEvent receivedEvent,
        IReadOnlyCollection<CashDenominationLine> denominations)
    {
        return new TerminalCashPaymentRequest(
            TerminalCashTenderId: tender.Id,
            CashCustodySessionId: tender.CashCustodySessionId,
            ParkingSessionId: ParseGuid(tender.ParkingSessionId, nameof(tender.ParkingSessionId)),
            TariffSnapshotId: ParseGuid(tender.TariffSnapshotId, nameof(tender.TariffSnapshotId)),
            CashierId: session.CashierId,
            CashierSessionReference: session.AuthenticatedCashierSessionReference,
            CashierShiftId: session.CashierShiftId,
            TerminalId: session.TerminalId,
            SiteId: ParseGuid(session.SiteId, nameof(session.SiteId)),
            SiteGroupId: ParseGuid(session.SiteGroupId, nameof(session.SiteGroupId)),
            PosServerId: session.PosServerId,
            Currency: tender.Currency,
            AmountDueMinorUnits: ToMinorUnits(tender.AmountDue),
            AmountTenderedMinorUnits: ToMinorUnits(tender.AmountTendered),
            ChangeDueMinorUnits: ToMinorUnits(tender.ChangeDue),
            CashReceivedAt: receivedEvent.OccurredAt,
            DenominationEntries: denominations
                .Where(denomination => denomination.Quantity > 0)
                .Select(denomination => new TerminalCashDenominationEntry(
                    denomination.DenominationCode,
                    ToMinorUnits(denomination.DenominationValue),
                    denomination.Quantity))
                .ToArray(),
            LocalEventReference: receivedEvent.Id.ToString("N"));
    }

    public static string Serialize(TerminalCashPaymentRequest payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    public static string ComputeHash(string payloadJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Guid ParseGuid(string value, string fieldName) =>
        Guid.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{fieldName} must be a GUID for Central PMS terminal cash-payment submission.");

    private static long ToMinorUnits(decimal amount) =>
        decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));
}
