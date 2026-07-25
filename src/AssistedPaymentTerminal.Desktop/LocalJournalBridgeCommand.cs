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
    public const string CentralPmsCashReceiptGetStatus = "centralPmsCashReceipt.getStatus";
    public const string CentralPmsCashReceiptRetrieveOrCheck = "centralPmsCashReceipt.retrieveOrCheck";
    public const string CentralPmsCashReceiptGetPreview = "centralPmsCashReceipt.getPreview";
    public const string CentralPmsCashReceiptPrintGetStatus = "centralPmsCashReceiptPrint.getStatus";
    public const string CentralPmsCashReceiptPrintSubmit = "centralPmsCashReceiptPrint.submit";
    public const string SalesInvoicePrintHistoryGetForTender = "salesInvoicePrintHistory.getForTender";
    public const string SalesInvoicePrintHistoryGetForFiscalDocument = "salesInvoicePrintHistory.getForFiscalDocument";
    public const string SalesInvoicePrintHistoryGetRecent = "salesInvoicePrintHistory.getRecent";
    public const string SalesInvoicePrintHistoryGetDetail = "salesInvoicePrintHistory.getDetail";
}
