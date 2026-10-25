namespace AssistedPaymentTerminal.LocalOperations;

public sealed class CashCustodyMovementEvent
{
    public Guid Id { get; set; }
    public Guid MovementId { get; set; }
    public CashCustodyMovement? Movement { get; set; }
    public CashCustodyMovementEventType EventType { get; set; }
    public required string AuthorizedActorId { get; set; }
    public required string ActorSessionReference { get; set; }
    public required string CorrelationId { get; set; }
    public required string IdempotencyKey { get; set; }
    public string? ReasonCode { get; set; }
    public string? ControlledMetadata { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
