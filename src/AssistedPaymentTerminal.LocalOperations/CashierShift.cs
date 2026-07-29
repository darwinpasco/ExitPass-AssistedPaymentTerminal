namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashierShift
{
    public string Id { get; set; } = string.Empty;

    public required string CashierId { get; set; }

    public required string AuthenticatedCashierSessionReference { get; set; }

    public required string TerminalId { get; set; }

    public required string SiteId { get; set; }

    public required string SiteGroupId { get; set; }

    public required string PosServerId { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public CashierShiftStatus Status { get; set; }
}
