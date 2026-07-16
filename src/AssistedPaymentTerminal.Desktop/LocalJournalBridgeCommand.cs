namespace AssistedPaymentTerminal.Desktop;

public static class LocalJournalBridgeCommand
{
    public const string Source = "apt-local-journal";
    public const string Health = "localJournal.health";
    public const string CreateOrGetDevelopmentSession = "localJournal.createOrGetDevelopmentSession";
    public const string StartTender = "localJournal.startTender";
    public const string RecordCashReceived = "localJournal.recordCashReceived";
    public const string ReadTenderByParkingSession = "localJournal.readTenderByParkingSession";
    public const string CentralPmsCashSubmissionGetStatus = "centralPmsCashSubmission.getStatus";
    public const string CentralPmsCashSubmissionSubmitOrReadback = "centralPmsCashSubmission.submitOrReadback";
    public const string CentralPmsCashFiscalGetStatus = "centralPmsCashFiscal.getStatus";
    public const string CentralPmsCashFiscalSubmitOrReadback = "centralPmsCashFiscal.submitOrReadback";
}
