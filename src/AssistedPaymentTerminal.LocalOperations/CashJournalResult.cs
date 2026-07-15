namespace AssistedPaymentTerminal.LocalOperations;

public sealed record CashJournalResult<T>
{
    private CashJournalResult(T value)
    {
        IsSuccess = true;
        Value = value;
    }

    private CashJournalResult(CashJournalError error)
    {
        IsSuccess = false;
        Error = error;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public CashJournalError? Error { get; }

    public static CashJournalResult<T> Success(T value) => new(value);

    public static CashJournalResult<T> Failure(CashJournalError error) => new(error);
}
