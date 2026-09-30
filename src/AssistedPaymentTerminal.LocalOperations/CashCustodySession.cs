namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashCustodySession
{
    public Guid Id { get; set; }

    public required string CashierId { get; set; }

    public required string AuthenticatedCashierSessionReference { get; set; }

    public required string CashierShiftId { get; set; }

    public required string TerminalId { get; set; }

    public required string SiteId { get; set; }

    public required string SiteGroupId { get; set; }

    public required string PosServerId { get; set; }

    public decimal OpeningCashAmount { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public decimal? ExpectedClosingCashAmount { get; set; }

    public decimal? ClosingCashAmount { get; set; }

    public decimal? VarianceAmount { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public string? ClosedByAuthenticatedCashierSessionReference { get; set; }

    public CashCustodySessionStatus Status { get; set; }

    public ICollection<CashTender> CashTenders { get; } = new List<CashTender>();
}
