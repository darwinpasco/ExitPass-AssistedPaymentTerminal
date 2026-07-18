using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CashReceiptPreviewProofHarnessTests
{
    [Fact]
    public async Task InteractiveAvailableProofPreservesSeededDatabaseAfterProcessExit()
    {
        var databasePath = TempDatabasePath();

        try
        {
            var result = await RunPreviewProofScriptAsync("-Interactive", "-Scenario", "Available", "-DatabasePath", databasePath);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("$env:APT_LOCAL_DB_PATH", result.Output, StringComparison.Ordinal);
            Assert.Contains("Seeded database exists: True", result.Output, StringComparison.Ordinal);
            Assert.Contains("Test-Path \"$env:APT_LOCAL_DB_PATH\"", result.Output, StringComparison.Ordinal);
            Assert.Contains("Remove-Item", result.Output, StringComparison.Ordinal);

            var printedPath = ExtractDatabasePath(result.Output);
            Assert.Equal(Path.GetFullPath(databasePath), printedPath);
            Assert.True(File.Exists(printedPath));

            var tenderId = ExtractTenderId(result.Output);
            var command = await ReadReceiptCommandAsync(printedPath, tenderId);
            Assert.NotNull(command);
            Assert.Equal(TerminalCashReceiptRetrievalStatus.Available, command!.Status);
            Assert.False(string.IsNullOrWhiteSpace(command.AuthoritativePresentationJson));
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task InteractiveVoidedProofPreservesReadableDatabaseAfterProcessExit()
    {
        var databasePath = TempDatabasePath();

        try
        {
            var result = await RunPreviewProofScriptAsync("-Interactive", "-Scenario", "Voided", "-DatabasePath", databasePath);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(databasePath));

            var tenderId = ExtractTenderId(result.Output);
            var command = await ReadReceiptCommandAsync(databasePath, tenderId);
            Assert.NotNull(command);
            Assert.Equal(TerminalCashReceiptRetrievalStatus.Voided, command!.Status);
            Assert.Equal("voided", command.VoidStatus);
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Theory]
    [InlineData("Complete", "success")]
    [InlineData("Voided", "success")]
    [InlineData("UnsupportedVersion", "receipt_preview_unsupported_version")]
    [InlineData("PayloadHashMismatch", "receipt_preview_integrity_failed")]
    [InlineData("MalformedPayload", "receipt_preview_decode_failed")]
    public async Task InteractivePreviewScenariosSeedCompletePrerequisiteChainAndReachPreviewBoundary(
        string scenario,
        string expectedPreviewResult)
    {
        var databasePath = TempDatabasePath();

        try
        {
            var result = await RunPreviewProofScriptAsync("-Interactive", "-Scenario", scenario, "-DatabasePath", databasePath);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(databasePath));

            var tenderId = ExtractTenderId(result.Output);
            var chain = await ReadChainStateAsync(databasePath, tenderId);
            Assert.Equal(TerminalCashPaymentCommandStatus.Confirmed, chain.PaymentStatus);
            Assert.Equal(TerminalCashFiscalCommandStatus.Recorded, chain.FiscalStatus);
            Assert.True(chain.PaymentAttemptId.HasValue);
            Assert.True(chain.PaymentConfirmationId.HasValue);
            Assert.True(chain.FiscalIssuanceReferenceId.HasValue);
            Assert.True(chain.PosFiscalDocumentId.HasValue);
            Assert.True(chain.ReceiptStatus is TerminalCashReceiptRetrievalStatus.Available or TerminalCashReceiptRetrievalStatus.Voided);
            Assert.False(string.IsNullOrWhiteSpace(chain.AuthoritativePresentationJson));

            var preview = ReceiptPreviewBuilder.Build(chain.ReceiptCommand!, ReceiptPreviewPaperProfiles.Select(null).Profile);
            if (expectedPreviewResult == "success")
            {
                Assert.True(preview.Success);
                Assert.NotNull(preview.Document);

                if (scenario == "Complete")
                {
                    Assert.False(preview.Document!.HasPlaceholders);
                    Assert.Equal("Complete", preview.Document.ConfigurationCompleteness);
                    var text = JsonSerializer.Serialize(preview.Document.Sections);
                    Assert.Contains("GOVERNED BIR ACCREDITATION DATE ISSUED", text, StringComparison.Ordinal);
                    Assert.Contains("GOVERNED BIR ACCREDITATION VALID UNTIL", text, StringComparison.Ordinal);
                    Assert.Contains("GOVERNED PTU DATE ISSUED", text, StringComparison.Ordinal);
                    Assert.Contains("birAccreditationIssuedDateDisplay", text, StringComparison.Ordinal);
                    Assert.Contains("ptuIssuedDateDisplay", text, StringComparison.Ordinal);
                }

                if (scenario == "Voided")
                {
                    Assert.True(preview.Document!.Voided);
                }
            }
            else
            {
                Assert.False(preview.Success);
                Assert.Equal(expectedPreviewResult, preview.ErrorCode);
            }
        }
        finally
        {
            DeleteDatabaseFiles(databasePath);
        }
    }

    [Fact]
    public async Task NonInteractiveProofStillCleansTemporaryDatabase()
    {
        var databasePath = TempDatabasePath();

        var result = await RunPreviewProofScriptAsync("-DatabasePath", databasePath);

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(databasePath));
        Assert.False(File.Exists($"{databasePath}-wal"));
        Assert.False(File.Exists($"{databasePath}-shm"));
    }

    private static async Task<TerminalCashReceiptRetrievalCommand?> ReadReceiptCommandAsync(
        string databasePath,
        Guid terminalCashTenderId)
    {
        var options = new LocalOperationsDatabaseOptions(
            databasePath,
            CentralPmsBaseUrl: "http://127.0.0.1:9",
            EnableCentralPmsCashSubmission: true,
            EnableCentralPmsFiscalIssuance: true,
            EnableCentralPmsReceiptRetrieval: true);

        var service = new TerminalCashReceiptRetrievalService(new ScriptedCentralPmsReceiptClient(), options);
        return await service.GetReceiptRetrievalByTenderAsync(terminalCashTenderId);
    }

    private static async Task<ReceiptScenarioChainState> ReadChainStateAsync(
        string databasePath,
        Guid terminalCashTenderId)
    {
        var options = new LocalOperationsDatabaseOptions(
            databasePath,
            CentralPmsBaseUrl: "http://127.0.0.1:9",
            EnableCentralPmsCashSubmission: true,
            EnableCentralPmsFiscalIssuance: true,
            EnableCentralPmsReceiptRetrieval: true);

        await using var dbContext = new CashJournalService(options).CreateDbContext();
        var payment = await dbContext.TerminalCashPaymentOutboxCommands
            .SingleAsync(command => command.TerminalCashTenderId == terminalCashTenderId);
        var fiscal = await dbContext.TerminalCashFiscalOutboxCommands
            .SingleAsync(command => command.TerminalCashTenderId == terminalCashTenderId);
        var receipt = await dbContext.TerminalCashReceiptRetrievalCommands
            .SingleAsync(command => command.TerminalCashTenderId == terminalCashTenderId);

        return new ReceiptScenarioChainState(
            payment.Status,
            fiscal.Status,
            receipt.Status,
            receipt.CanonicalPaymentAttemptId,
            receipt.CanonicalPaymentConfirmationId,
            receipt.FiscalIssuanceReferenceId,
            receipt.PosFiscalDocumentId,
            receipt.AuthoritativePresentationJson,
            receipt);
    }

    private static string ExtractDatabasePath(string output)
    {
        var match = Regex.Match(output, "\\$env:APT_LOCAL_DB_PATH = \"(?<path>[^\"]+)\"");
        Assert.True(match.Success, output);
        return Path.GetFullPath(match.Groups["path"].Value);
    }

    private static Guid ExtractTenderId(string output)
    {
        var match = Regex.Match(output, "Seeded terminal cash tender: (?<id>[0-9a-fA-F-]{36})");
        Assert.True(match.Success, output);
        return Guid.Parse(match.Groups["id"].Value);
    }

    private static async Task<ProofProcessResult> RunPreviewProofScriptAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("powershell")
        {
            WorkingDirectory = RepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "Invoke-CentralPmsCashReceiptPreviewUiProof.ps1"));
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell proof process did not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var output = await outputTask;
        var error = await errorTask;
        return new ProofProcessResult(process.ExitCode, $"{output}{Environment.NewLine}{error}");
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "scripts", "Invoke-CentralPmsCashReceiptPreviewUiProof.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root could not be located from the test output directory.");
    }

    private static string TempDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"exitpass-apt-receipt-preview-harness-test-{Guid.NewGuid():N}.db");

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed record ProofProcessResult(int ExitCode, string Output);

    private sealed record ReceiptScenarioChainState(
        TerminalCashPaymentCommandStatus PaymentStatus,
        TerminalCashFiscalCommandStatus FiscalStatus,
        TerminalCashReceiptRetrievalStatus ReceiptStatus,
        Guid? PaymentAttemptId,
        Guid? PaymentConfirmationId,
        Guid? FiscalIssuanceReferenceId,
        Guid? PosFiscalDocumentId,
        string? AuthoritativePresentationJson,
        TerminalCashReceiptRetrievalCommand? ReceiptCommand);
}
