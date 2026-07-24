namespace AssistedPaymentTerminal.LocalOperations;

public enum TerminalCashReceiptPrintClassification
{
    Original = 0,
    Reprint = 1
}

public enum TerminalCashReceiptPrintJobStatus
{
    Requested = 0,
    Preparing = 1,
    PrinterUnavailable = 2,
    PreparationFailed = 3,
    SubmissionPending = 4,
    SubmittedToSpooler = 5,
    SpoolerSubmissionFailed = 6,
    UnknownAfterRestart = 7,
    Completed = 8
}
