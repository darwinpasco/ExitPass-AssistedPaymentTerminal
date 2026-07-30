using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AssistedPaymentTerminal.LocalOperations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AssistedPaymentTerminal.PlaintextDatabaseMigration.ValidationHarness;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = HarnessOptions.Parse(args);
        if (!options.Acknowledged)
        {
            Console.WriteLine("Status: BLOCKED");
            Console.WriteLine("Reason: validation-only acknowledgment is required.");
            return 2;
        }

        var rootCheck = ValidateRoot(options.ValidationRoot);
        if (rootCheck is not null)
        {
            Console.WriteLine("Status: BLOCKED");
            Console.WriteLine($"Reason: {rootCheck}");
            return 2;
        }

        if (options.Command.Equals("prepare-wrong-user-proof", StringComparison.OrdinalIgnoreCase))
        {
            var result = await WrongUserDpapiProof.PrepareAsync(options).ConfigureAwait(false);
            Console.WriteLine(WrongUserDpapiProof.ToJson(result));
            return result.Result == "PREPARED" ? 0 : 2;
        }

        if (options.Command.Equals("verify-wrong-user-proof", StringComparison.OrdinalIgnoreCase))
        {
            var result = await WrongUserDpapiProof.VerifyAsync(options.ValidationRoot).ConfigureAwait(false);
            Console.WriteLine(WrongUserDpapiProof.ToJson(result));
            return result.Classification == "KeyEnvelopeWrongIdentity" ? 2 : 1;
        }

        if (options.Command.Equals("cleanup-wrong-user-proof", StringComparison.OrdinalIgnoreCase))
        {
            WrongUserDpapiProof.Cleanup(options.ValidationRoot);
            Console.WriteLine(WrongUserDpapiProof.ToJson(new WrongUserProofResult(
                "CLEANED",
                "Wrong-user DPAPI proof artifacts were removed from the disposable validation root.",
                null,
                null,
                null,
                null,
                null,
                null,
                null)));
            return 0;
        }

        var harness = new MigrationValidationHarness(options);
        var summary = await harness.RunAsync().ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));

        return summary.Scenarios.Any(scenario => scenario.Result == "FAILED")
            ? 2
            : summary.Scenarios.Any(scenario => scenario.Result == "BLOCKED")
                ? 3
                : 0;
    }

    private static string? ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return "A disposable validation root is required.";
        }

        var fullRoot = Path.GetFullPath(root);
        var repositoryRoot = FindRepositoryRoot(Directory.GetCurrentDirectory());
        if (repositoryRoot is not null &&
            fullRoot.StartsWith(repositoryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return "The validation root must not be inside this repository.";
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var operationalRoot = Path.Combine(localAppData, "ExitPass", "AssistedPaymentTerminal");
        if (fullRoot.StartsWith(operationalRoot, StringComparison.OrdinalIgnoreCase))
        {
            return "The validation root must not be inside the operational APT LocalAppData directory.";
        }

        return null;
    }

    private static string? FindRepositoryRoot(string start)
    {
        var current = Path.GetFullPath(start);
        while (!string.IsNullOrWhiteSpace(current))
        {
            if (Directory.Exists(Path.Combine(current, ".git")))
            {
                return current;
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }
}

public sealed record HarnessSummary(
    string WindowsUser,
    string ValidationRoot,
    IReadOnlyList<ScenarioResult> Scenarios);

public sealed record ScenarioResult(
    int Number,
    string Name,
    string Result,
    string Detail);

public sealed record HarnessOptions(
    bool Acknowledged,
    string ValidationRoot,
    string Scenario,
    bool PreserveArtifacts,
    string Command,
    string? AlternateUser)
{
    public static HarnessOptions Parse(IReadOnlyList<string> args)
    {
        var acknowledged = false;
        var root = string.Empty;
        var scenario = "All";
        var preserve = false;
        var command = "run-scenarios";
        string? alternateUser = null;

        for (var index = 0; index < args.Count; index++)
        {
            var value = args[index];
            if (string.Equals(value, "--acknowledge-validation-only", StringComparison.OrdinalIgnoreCase))
            {
                acknowledged = true;
                continue;
            }

            if (string.Equals(value, "--validation-root", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                root = args[++index];
                continue;
            }

            if (value.StartsWith("--validation-root=", StringComparison.OrdinalIgnoreCase))
            {
                root = value["--validation-root=".Length..];
                continue;
            }

            if (string.Equals(value, "--scenario", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                scenario = args[++index];
                continue;
            }

            if (string.Equals(value, "--preserve-artifacts-on-failure", StringComparison.OrdinalIgnoreCase))
            {
                preserve = true;
                continue;
            }

            if (string.Equals(value, "--command", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                command = args[++index];
                continue;
            }

            if (value.StartsWith("--command=", StringComparison.OrdinalIgnoreCase))
            {
                command = value["--command=".Length..];
                continue;
            }

            if (string.Equals(value, "--alternate-user", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                alternateUser = args[++index];
                continue;
            }

            if (value.StartsWith("--alternate-user=", StringComparison.OrdinalIgnoreCase))
            {
                alternateUser = value["--alternate-user=".Length..];
            }
        }

        return new HarnessOptions(acknowledged, root, scenario, preserve, command, alternateUser);
    }
}

public sealed record WrongUserProofManifest(
    string OperationId,
    string DatabasePath,
    string EnvelopePath,
    string ExpectedClassification,
    string DatabaseSha256,
    string EnvelopeSha256,
    string CreatingUserReference,
    string ResultDirectory,
    DateTimeOffset CreatedAt);

public sealed record WrongUserProofResult(
    string Result,
    string Message,
    string? Classification,
    string? OperationId,
    string? DatabasePath,
    string? EnvelopePath,
    string? CreatingUserReference,
    string? VerifyingUserReference,
    string? ResultDirectory);

public static class WrongUserDpapiProof
{
    private const FileSystemRights ProofDirectoryRequiredRights = FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory;
    private const FileSystemRights ProofArtifactRequiredRights = FileSystemRights.ReadAndExecute;
    private const FileSystemRights ResultDirectoryRequiredRights = FileSystemRights.Modify;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<WrongUserProofResult> PrepareAsync(HarnessOptions options)
    {
        var proofDirectory = ProofDirectory(options.ValidationRoot);
        var databasePath = Path.Combine(proofDirectory, "LocalOperations", "cash-journal.db");
        var envelopePath = Path.Combine(Path.GetDirectoryName(databasePath)!, LocalDatabaseKeyEnvelope.EnvelopeFileName);
        var manifestPath = ManifestPath(options.ValidationRoot);
        var resultDirectory = Path.Combine(proofDirectory, "AlternateUserResult");

        if (Directory.Exists(proofDirectory))
        {
            return new WrongUserProofResult(
                "BLOCKED",
                "Wrong-user proof directory already exists. Run cleanup-wrong-user-proof before preparing a new artifact.",
                null,
                null,
                databasePath,
                envelopePath,
                SafeWindowsIdentityReference(),
                null,
                resultDirectory);
        }

        var service = new CashJournalService(new LocalOperationsDatabaseOptions(
            DatabasePath: databasePath,
            CentralPmsBaseUrl: "UNCONFIGURED_CENTRAL_PMS"));
        await service.InitializeAsync().ConfigureAwait(false);

        Directory.CreateDirectory(resultDirectory);
        if (!string.IsNullOrWhiteSpace(options.AlternateUser))
        {
            var aclResult = GrantAlternateUserAccess(proofDirectory, resultDirectory, options.AlternateUser);
            if (!aclResult.Succeeded)
            {
                return new WrongUserProofResult(
                    "BLOCKED",
                    aclResult.SafeMessage,
                    aclResult.Classification,
                    null,
                    databasePath,
                    envelopePath,
                    SafeWindowsIdentityReference(),
                    null,
                    resultDirectory);
            }
        }

        var manifest = new WrongUserProofManifest(
            Guid.NewGuid().ToString("D"),
            databasePath,
            envelopePath,
            "KeyEnvelopeWrongIdentity",
            Sha256(databasePath),
            Sha256(envelopePath),
            SafeWindowsIdentityReference(),
            resultDirectory,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8).ConfigureAwait(false);

        return new WrongUserProofResult(
            "PREPARED",
            "Wrong-user DPAPI proof artifact prepared with real CurrentUser protection.",
            manifest.ExpectedClassification,
            manifest.OperationId,
            manifest.DatabasePath,
            manifest.EnvelopePath,
            manifest.CreatingUserReference,
            null,
            manifest.ResultDirectory);
    }

    public static async Task<WrongUserProofResult> VerifyAsync(string validationRoot)
    {
        var manifestPath = ManifestPath(validationRoot);
        if (!File.Exists(manifestPath))
        {
            return new WrongUserProofResult(
                "BLOCKED",
                "Wrong-user proof manifest was not found.",
                null,
                null,
                null,
                null,
                null,
                SafeWindowsIdentityReference(),
                null);
        }

        var manifest = JsonSerializer.Deserialize<WrongUserProofManifest>(
            await File.ReadAllTextAsync(manifestPath, Encoding.UTF8).ConfigureAwait(false),
            JsonOptions);
        if (manifest is null)
        {
            return new WrongUserProofResult(
                "BLOCKED",
                "Wrong-user proof manifest could not be parsed.",
                null,
                null,
                null,
                null,
                null,
                SafeWindowsIdentityReference(),
                null);
        }

        var manager = new LocalDatabaseEncryptionManager(manifest.DatabasePath);
        var envelope = LocalDatabaseKeyEnvelope.Parse(await File.ReadAllTextAsync(manifest.EnvelopePath, Encoding.UTF8).ConfigureAwait(false));
        envelope.Validate(manager.DatabaseIdentity);
        var protectedKey = envelope.DecodeProtectedKey();
        try
        {
            var key = new DpapiCurrentUserLocalDatabaseKeyProtector().Unprotect(protectedKey, LocalDatabaseKeyEnvelope.EntropyBytes);
            CryptographicOperations.ZeroMemory(key);
            return new WrongUserProofResult(
                "FAILED",
                "The protected key envelope was unprotected by this Windows user. This is expected only for the creating user.",
                "EnvelopeUnprotected",
                manifest.OperationId,
                manifest.DatabasePath,
                manifest.EnvelopePath,
                manifest.CreatingUserReference,
                SafeWindowsIdentityReference(),
                manifest.ResultDirectory);
        }
        catch (CryptographicException)
        {
            return new WrongUserProofResult(
                "PASSED",
                "DPAPI CurrentUser rejected the protected key envelope for this Windows user.",
                "KeyEnvelopeWrongIdentity",
                manifest.OperationId,
                manifest.DatabasePath,
                manifest.EnvelopePath,
                manifest.CreatingUserReference,
                SafeWindowsIdentityReference(),
                manifest.ResultDirectory);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    public static void Cleanup(string validationRoot)
    {
        var proofDirectory = ProofDirectory(validationRoot);
        if (Directory.Exists(proofDirectory))
        {
            Directory.Delete(proofDirectory, recursive: true);
        }
    }

    public static string ToJson(WrongUserProofResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);

    private static string ProofDirectory(string validationRoot) =>
        Path.Combine(Path.GetFullPath(validationRoot), "WrongUserDpapiProof");

    private static string ManifestPath(string validationRoot) =>
        Path.Combine(ProofDirectory(validationRoot), "wrong-user-proof-manifest.json");

    public static string NormalizeLocalAccountName(string accountName)
    {
        var trimmed = accountName.Trim();
        if (trimmed.StartsWith(".\\", StringComparison.Ordinal))
        {
            return string.Concat(Environment.MachineName, "\\", trimmed[2..]);
        }

        if (!trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return string.Concat(Environment.MachineName, "\\", trimmed);
        }

        var parts = trimmed.Split('\\', 2);
        return parts[0] == "."
            ? string.Concat(Environment.MachineName, "\\", parts[1])
            : trimmed;
    }

    public static WrongUserAclSetupResult ResolveAlternateUserSid(string accountName)
    {
        try
        {
            var normalized = NormalizeLocalAccountName(accountName);
            var account = new NTAccount(normalized);
            var identity = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            return WrongUserAclSetupResult.Success(identity.Value);
        }
        catch (IdentityNotMappedException)
        {
            return WrongUserAclSetupResult.Blocked(
                "AlternateAccountNotFound",
                "The disposable alternate Windows account could not be resolved.");
        }
        catch (SystemException)
        {
            return WrongUserAclSetupResult.Blocked(
                "AlternateAccountSidTranslationFailed",
                "The disposable alternate Windows account could not be translated to a SID.");
        }
    }

    public static WrongUserAclSetupResult GrantAlternateUserAccess(string proofDirectory, string resultDirectory, string alternateUser)
    {
        if (!Directory.Exists(proofDirectory))
        {
            return WrongUserAclSetupResult.Blocked(
                "ProofDirectoryMissing",
                "The wrong-user proof directory was not available for ACL setup.");
        }

        if (!Directory.Exists(resultDirectory))
        {
            return WrongUserAclSetupResult.Blocked(
                "ResultDirectoryMissing",
                "The wrong-user result directory was not available for ACL setup.");
        }

        var sidResult = ResolveAlternateUserSid(alternateUser);
        if (!sidResult.Succeeded)
        {
            return sidResult;
        }

        var sid = new SecurityIdentifier(sidResult.Sid!);
        var proofGrant = ApplyDirectoryAllowRule(proofDirectory, sid, ProofDirectoryRequiredRights);
        if (!proofGrant.Succeeded)
        {
            return WrongUserAclSetupResult.Blocked(
                "ProofDirectoryAclFailed",
                proofGrant.SafeMessage);
        }

        var resultGrant = ApplyDirectoryAllowRule(resultDirectory, sid, ResultDirectoryRequiredRights);
        if (!resultGrant.Succeeded)
        {
            return WrongUserAclSetupResult.Blocked(
                "ResultDirectoryAclFailed",
                resultGrant.SafeMessage);
        }

        var verification = VerifyAlternateUserAccess(proofDirectory, resultDirectory, sidResult.Sid!);
        if (!verification.Succeeded)
        {
            return verification;
        }

        return sidResult;
    }

    public static string CreateProofDirectoryReadRule(string sid) =>
        string.Concat("*", sid, ":(OI)(CI)RX");

    public static string CreateResultDirectoryWriteRule(string sid) =>
        string.Concat("*", sid, ":(OI)(CI)M");

    public static WrongUserAclSetupResult VerifyAlternateUserAccess(string proofDirectory, string resultDirectory, string sidValue)
    {
        try
        {
            var sid = new SecurityIdentifier(sidValue);
            var proofAccess = VerifyDirectoryAccess(
                proofDirectory,
                sid,
                ProofDirectoryRequiredRights,
                "ProofDirectoryAceMissing",
                "ProofDirectoryRightsInsufficient");
            if (!proofAccess.Succeeded)
            {
                return proofAccess;
            }

            var artifactAccess = VerifyProofArtifactAccess(proofDirectory, resultDirectory, sid);
            if (!artifactAccess.Succeeded)
            {
                return artifactAccess;
            }

            var resultAccess = VerifyDirectoryAccess(
                resultDirectory,
                sid,
                ResultDirectoryRequiredRights,
                "ResultDirectoryAceMissing",
                "ResultDirectoryRightsInsufficient");
            if (!resultAccess.Succeeded)
            {
                return resultAccess;
            }

            return WrongUserAclSetupResult.Success(sidValue);
        }
        catch (SystemException)
        {
            return WrongUserAclSetupResult.Blocked(
                "AclReadbackFailed",
                "The disposable proof ACL could not be read back for verification.");
        }
    }

    public static bool IdentityReferenceMatchesSid(IdentityReference identity, string sidValue)
    {
        if (identity is SecurityIdentifier securityIdentifier)
        {
            return string.Equals(securityIdentifier.Value, sidValue, StringComparison.OrdinalIgnoreCase);
        }

        if (identity is NTAccount account)
        {
            try
            {
                var translated = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
                return string.Equals(translated.Value, sidValue, StringComparison.OrdinalIgnoreCase);
            }
            catch (SystemException)
            {
                return false;
            }
        }

        return false;
    }

    public static bool RightsInclude(FileSystemRights actual, FileSystemRights required) =>
        (actual & required) == required;

    private static WrongUserAclSetupResult ApplyDirectoryAllowRule(string path, SecurityIdentifier sid, FileSystemRights rights)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            var security = directory.GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directory.SetAccessControl(security);
            return WrongUserAclSetupResult.Success(sid.Value);
        }
        catch (SystemException)
        {
            return WrongUserAclSetupResult.Blocked(
                "AclWriteFailed",
                "The disposable proof ACL could not be written.");
        }
    }

    private static WrongUserAclSetupResult VerifyProofArtifactAccess(
        string proofDirectory,
        string resultDirectory,
        SecurityIdentifier sid)
    {
        foreach (var file in Directory.EnumerateFiles(proofDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var artifactAccess = VerifyFileAccess(
                file,
                sid,
                ProofArtifactRequiredRights,
                "ProofArtifactInheritanceMissing",
                "ProofArtifactInheritanceMissing");
            if (!artifactAccess.Succeeded)
            {
                return artifactAccess;
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(proofDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(resultDirectory), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var directoryAccess = VerifyDirectoryAccess(
                directory,
                sid,
                ProofDirectoryRequiredRights,
                "ProofArtifactInheritanceMissing",
                "ProofArtifactInheritanceMissing");
            if (!directoryAccess.Succeeded)
            {
                return directoryAccess;
            }
        }

        return WrongUserAclSetupResult.Success(sid.Value);
    }

    private static WrongUserAclSetupResult VerifyDirectoryAccess(
        string path,
        SecurityIdentifier sid,
        FileSystemRights requiredRights,
        string missingClassification,
        string insufficientClassification)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        return VerifyRules(
            security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)),
            sid,
            requiredRights,
            missingClassification,
            insufficientClassification);
    }

    private static WrongUserAclSetupResult VerifyFileAccess(
        string path,
        SecurityIdentifier sid,
        FileSystemRights requiredRights,
        string missingClassification,
        string insufficientClassification)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl(AccessControlSections.Access);
        return VerifyRules(
            security.GetAccessRules(includeExplicit: true, includeInherited: true, targetType: typeof(SecurityIdentifier)),
            sid,
            requiredRights,
            missingClassification,
            insufficientClassification);
    }

    private static WrongUserAclSetupResult VerifyRules(
        AuthorizationRuleCollection rules,
        SecurityIdentifier sid,
        FileSystemRights requiredRights,
        string missingClassification,
        string insufficientClassification)
    {
        var allowRights = default(FileSystemRights);
        var matchingAllowCount = 0;

        foreach (FileSystemAccessRule rule in rules)
        {
            if (!IdentityReferenceMatchesSid(rule.IdentityReference, sid.Value))
            {
                continue;
            }

            if (rule.AccessControlType == AccessControlType.Deny && (rule.FileSystemRights & requiredRights) != 0)
            {
                return WrongUserAclSetupResult.Blocked(
                    "ConflictingDenyRule",
                    "The disposable proof ACL contains a deny rule that conflicts with the required access.");
            }

            if (rule.AccessControlType == AccessControlType.Allow)
            {
                matchingAllowCount++;
                allowRights |= rule.FileSystemRights;
            }
        }

        if (matchingAllowCount == 0)
        {
            return WrongUserAclSetupResult.Blocked(
                missingClassification,
                "The disposable proof ACL did not contain the expected alternate-user SID.");
        }

        if (!RightsInclude(allowRights, requiredRights))
        {
            return WrongUserAclSetupResult.Blocked(
                insufficientClassification,
                "The disposable proof ACL does not provide the required bounded access.");
        }

        return WrongUserAclSetupResult.Success(sid.Value);
    }

    private static IcaclsResult RunIcacls(string path, string operation, string rule, bool recursive)
    {
        var startInfo = new ProcessStartInfo("icacls")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(path);
        if (!string.IsNullOrWhiteSpace(operation))
        {
            startInfo.ArgumentList.Add(operation);
        }

        if (!string.IsNullOrWhiteSpace(rule))
        {
            startInfo.ArgumentList.Add(rule);
        }

        if (recursive)
        {
            startInfo.ArgumentList.Add("/T");
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("icacls could not be started.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new IcaclsResult(process.ExitCode == 0, process.ExitCode, stdout, stderr);
    }

    private static string SafeWindowsIdentityReference()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? string.Concat(Environment.UserDomainName, "\\", Environment.UserName);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
    }

    private static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

public sealed record WrongUserAclSetupResult(
    bool Succeeded,
    string? Sid,
    string Classification,
    string SafeMessage)
{
    public static WrongUserAclSetupResult Success(string sid) =>
        new(true, sid, "AclReady", "Disposable proof ACL setup completed.");

    public static WrongUserAclSetupResult Blocked(string classification, string safeMessage) =>
        new(false, null, classification, safeMessage);
}

internal sealed record IcaclsResult(
    bool Succeeded,
    int ExitCode,
    string Stdout,
    string Stderr);

internal sealed class MigrationValidationHarness(HarnessOptions options)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-30T00:00:00Z");

    public async Task<HarnessSummary> RunAsync()
    {
        var root = Path.GetFullPath(options.ValidationRoot);
        Directory.CreateDirectory(root);
        var scenarios = new List<ScenarioResult>();
        var synthetic = "All".Equals(options.Scenario, StringComparison.OrdinalIgnoreCase);

        if (synthetic || options.Scenario.Equals("Core", StringComparison.OrdinalIgnoreCase))
        {
            await AddCoreScenariosAsync(root, scenarios).ConfigureAwait(false);
        }

        if (synthetic || options.Scenario.Equals("Failures", StringComparison.OrdinalIgnoreCase))
        {
            await AddFailureScenariosAsync(root, scenarios).ConfigureAwait(false);
        }

        if (synthetic || options.Scenario.Equals("Interruptions", StringComparison.OrdinalIgnoreCase))
        {
            await AddInterruptionScenariosAsync(root, scenarios).ConfigureAwait(false);
        }

        if (synthetic || options.Scenario.Equals("Recovery", StringComparison.OrdinalIgnoreCase))
        {
            await AddOperationalRecoveryScenariosAsync(root, scenarios).ConfigureAwait(false);
        }

        if (synthetic || options.Scenario.Equals("WrongUser", StringComparison.OrdinalIgnoreCase))
        {
            scenarios.Add(new ScenarioResult(
                13,
                "Different Windows user DPAPI CurrentUser readback",
                "BLOCKED",
                "No alternate Windows profile was invoked by this harness run. Prepare with this harness under the primary user, then run the maintenance tool dry classification under an approved disposable second Windows account against the same disposable database path."));
        }

        if (!options.PreserveArtifacts && scenarios.All(scenario => scenario.Result != "FAILED"))
        {
            TryDelete(root);
        }

        return new HarnessSummary(
            string.Concat(Environment.UserDomainName, "\\", Environment.UserName),
            root,
            scenarios.OrderBy(scenario => scenario.Number).ToArray());
    }

    private static async Task AddCoreScenariosAsync(string root, List<ScenarioResult> scenarios)
    {
        var full = await RunSuccessfulMigrationAsync(root, "scenario-01-full", FixtureProfile.Full).ConfigureAwait(false);
        scenarios.Add(PassIf(1, "Supported plaintext database migrates successfully", full.Completed, full.Detail));
        scenarios.Add(PassIf(4, "Encrypted target has non-plaintext header", !HasPlainSqliteHeader(full.DatabasePath), full.Detail));
        scenarios.Add(PassIf(5, "Opening migrated database without key fails", NoKeyOpenFails(full.DatabasePath), full.Detail));
        scenarios.Add(PassIf(6, "Opening migrated database with same-user envelope succeeds", full.PersistenceReady, full.Detail));
        scenarios.Add(PassIf(7, "Normal encrypted APT initialization succeeds after migration", full.PersistenceReady, full.Detail));

        var databaseHash = Sha256(full.DatabasePath);
        var envelopeHash = Sha256(EnvelopePath(full.DatabasePath));
        var repeated = await Service(full.DatabasePath, authorized: true).MigrateAsync().ConfigureAwait(false);
        scenarios.Add(PassIf(
            8,
            "Repeated migration after completion is idempotent",
            repeated.Status == LocalDatabasePlaintextMigrationStatus.MigrationAlreadyCompleted
            && databaseHash == Sha256(full.DatabasePath)
            && envelopeHash == Sha256(EnvelopePath(full.DatabasePath)),
            repeated.Status.ToString()));

        var wal = await RunSuccessfulMigrationAsync(root, "scenario-02-wal", FixtureProfile.Wal).ConfigureAwait(false);
        scenarios.Add(PassIf(2, "Committed WAL records survive", wal.Completed && wal.ActiveShiftCount == 2, wal.Detail));

        var walShm = await RunSuccessfulMigrationAsync(root, "scenario-03-wal-shm", FixtureProfile.WalAndShm).ConfigureAwait(false);
        scenarios.Add(PassIf(3, "WAL and SHM source posture handled", walShm.Completed && walShm.ActiveShiftCount >= 2, walShm.Detail));
    }

    private static async Task AddFailureScenariosAsync(string root, List<ScenarioResult> scenarios)
    {
        scenarios.Add(await ExpectStatusAsync(9, root, "Application running rejects migration", "scenario-09-app-running", FixtureProfile.Minimum, LocalDatabasePlaintextMigrationStatus.ApplicationRunning, authorized: true, isAppRunning: () => true).ConfigureAwait(false));

        var lockedPath = DatabasePath(root, "scenario-10-locked");
        await FixtureBuilder.CreateAsync(lockedPath, FixtureProfile.Minimum).ConfigureAwait(false);
        using (new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = await Service(lockedPath, authorized: true).MigrateAsync().ConfigureAwait(false);
            scenarios.Add(PassIf(10, "Database locked by another process is rejected", locked.Status == LocalDatabasePlaintextMigrationStatus.SourceLocked, locked.Status.ToString()));
        }

        scenarios.Add(await ExpectStatusAsync(11, root, "Authorization flag missing rejects migration", "scenario-11-authorization", FixtureProfile.Minimum, LocalDatabasePlaintextMigrationStatus.BlockedForSupport).ConfigureAwait(false));

        var lockPath = DatabasePath(root, "scenario-12-migration-lock");
        await FixtureBuilder.CreateAsync(lockPath, FixtureProfile.Minimum).ConfigureAwait(false);
        var migrationLock = Path.Combine(Path.GetDirectoryName(lockPath)!, "PlaintextMigration", "cash-journal-migration.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(migrationLock)!);
        using (new FileStream(migrationLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await Service(lockPath, authorized: true).MigrateAsync().ConfigureAwait(false);
            scenarios.Add(PassIf(12, "Concurrent migration lock rejects second operation", result.Status == LocalDatabasePlaintextMigrationStatus.SourceLocked, result.Status.ToString()));
        }

        scenarios.Add(await ExistingEnvelopeConflictAsync(root).ConfigureAwait(false));
        scenarios.Add(await ExistingTargetConflictAsync(root).ConfigureAwait(false));
        scenarios.Add(await ExistingBackupConflictAsync(root).ConfigureAwait(false));
        scenarios.Add(await ExpectStatusAsync(17, root, "Unsupported plaintext schema blocks", "scenario-17-unsupported", FixtureProfile.Unsupported, LocalDatabasePlaintextMigrationStatus.UnsupportedSchema, authorized: true).ConfigureAwait(false));
        scenarios.Add(await ExpectStatusAsync(18, root, "Corrupt plaintext database blocks", "scenario-18-corrupt", FixtureProfile.CorruptHeader, LocalDatabasePlaintextMigrationStatus.SourceCorrupt, authorized: true).ConfigureAwait(false));
        scenarios.Add(await ExpectStatusAsync(19, root, "Foreign-key integrity failure blocks", "scenario-19-fk", FixtureProfile.ForeignKeyFailure, LocalDatabasePlaintextMigrationStatus.SourceCorrupt, authorized: true).ConfigureAwait(false));
        scenarios.Add(await ExpectStatusAsync(20, root, "Required table inventory mismatch blocks", "scenario-20-inventory", FixtureProfile.Unsupported, LocalDatabasePlaintextMigrationStatus.UnsupportedSchema, authorized: true).ConfigureAwait(false));
        scenarios.Add(await SourceChangeBlocksAsync(root).ConfigureAwait(false));
        scenarios.Add(await ExpectStatusAsync(22, root, "Insufficient disk simulation blocks", "scenario-22-disk", FixtureProfile.Minimum, LocalDatabasePlaintextMigrationStatus.InsufficientDisk, authorized: true, availableDiskBytes: () => 1).ConfigureAwait(false));
    }

    private static async Task AddInterruptionScenariosAsync(string root, List<ScenarioResult> scenarios)
    {
        scenarios.Add(await InterruptAndResumeAsync(23, root, "Interrupt during backup", LocalDatabasePlaintextMigrationPhase.BackupStarted, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase).ConfigureAwait(false));
        scenarios.Add(await InterruptAndResumeAsync(24, root, "Interrupt during SQLCipher export", LocalDatabasePlaintextMigrationPhase.ExportStarted, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase).ConfigureAwait(false));
        scenarios.Add(await InterruptAndResumeAsync(25, root, "Interrupt after export before target verification", LocalDatabasePlaintextMigrationPhase.ExportCompleted, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase).ConfigureAwait(false));
        scenarios.Add(await InterruptAndResumeAsync(26, root, "Interrupt after target verification before envelope publication", LocalDatabasePlaintextMigrationPhase.TargetVerified, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase).ConfigureAwait(false));
        scenarios.Add(await InterruptAndResumeAsync(27, root, "Interrupt after active database switch before envelope switch", LocalDatabasePlaintextMigrationPhase.CutoverStarted, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase).ConfigureAwait(false));
        scenarios.Add(await InterruptAndResumeAsync(28, root, "Interrupt after envelope switch before post-cutover verification", LocalDatabasePlaintextMigrationPhase.EnvelopeSwitched, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase).ConfigureAwait(false));
        scenarios.Add(await InterruptAndResumeAsync(29, root, "Restart after interruption recovers deterministically", LocalDatabasePlaintextMigrationPhase.PostCutoverVerificationStarted, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase).ConfigureAwait(false));
        scenarios.Add(await InterruptAndResumeAsync(30, root, "Interrupted state never permits plaintext fallback", LocalDatabasePlaintextMigrationPhase.DatabaseSwitched, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase).ConfigureAwait(false));

        scenarios.Add(await RollbackRequiredAndCompletedAsync(31, root, "Failure requires rollback").ConfigureAwait(false));
        scenarios.Add(await RollbackRequiredAndCompletedAsync(32, root, "Rollback completes successfully").ConfigureAwait(false));
        scenarios.Add(await RollbackInterruptionAsync(root).ConfigureAwait(false));
        scenarios.Add(await RollbackRequiredAndCompletedAsync(34, root, "Repeated invocation after rollback remains deterministic").ConfigureAwait(false));
        scenarios.Add(await RollbackRequiredAndCompletedAsync(35, root, "Rollback preserves verified source or backup").ConfigureAwait(false));
        scenarios.Add(await RollbackRequiredAndCompletedAsync(36, root, "Incomplete encrypted artifacts are quarantined").ConfigureAwait(false));
        scenarios.Add(await RollbackRequiredAndCompletedAsync(37, root, "Normal startup after rollback fails closed on plaintext").ConfigureAwait(false));
    }

    private static async Task AddOperationalRecoveryScenariosAsync(string root, List<ScenarioResult> scenarios)
    {
        var full = await RunSuccessfulMigrationAsync(root, "scenario-38-50-operational", FixtureProfile.Full).ConfigureAwait(false);
        scenarios.Add(PassIf(38, "Open cashier shift preserved", full.ActiveShiftCount == 1, full.Detail));
        scenarios.Add(PassIf(39, "Open cash-custody session preserved", full.ActiveCustodyCount == 1, full.Detail));
        scenarios.Add(PassIf(40, "Cash tender and tender-event records preserved", full.TargetRows.GetValueOrDefault("cash_tenders") == 1 && full.TargetRows.GetValueOrDefault("cash_tender_events") == 1, full.Detail));
        scenarios.Add(PassIf(41, "Denomination records and totals preserved", full.TargetRows.GetValueOrDefault("cash_denomination_entries") == 2, full.Detail));
        scenarios.Add(PassIf(42, "Payment outbox and attempts preserved", full.TargetRows.GetValueOrDefault("terminal_cash_payment_outbox_commands") == 1 && full.TargetRows.GetValueOrDefault("terminal_cash_payment_submission_attempts") == 1, full.Detail));
        scenarios.Add(PassIf(43, "Fiscal issuance recovery state preserved", full.TargetRows.GetValueOrDefault("terminal_cash_fiscal_outbox_commands") == 1 && full.TargetRows.GetValueOrDefault("terminal_cash_fiscal_attempts") == 1, full.Detail));
        scenarios.Add(PassIf(44, "Receipt retrieval recovery state preserved", full.TargetRows.GetValueOrDefault("terminal_cash_receipt_retrieval_commands") == 1 && full.TargetRows.GetValueOrDefault("terminal_cash_receipt_retrieval_attempts") == 1, full.Detail));
        scenarios.Add(PassIf(45, "Print jobs and print history preserved", full.TargetRows.GetValueOrDefault("terminal_cash_receipt_print_jobs") == 1, full.Detail));
        scenarios.Add(PassIf(46, "Payable-basis recovery state preserved", full.TargetRows.GetValueOrDefault("terminal_cash_payable_basis_states") == 1, full.Detail));
        scenarios.Add(PassIf(47, "Statutory recovery state preserved", full.StatutoryStatePreserved, full.Detail));
        scenarios.Add(PassIf(48, "Schema metadata and migration state preserved", full.Completed && File.Exists(StatePath(full.DatabasePath)), full.Detail));
        scenarios.Add(PassIf(49, "No duplicate business rows after migration", full.NoDuplicateRows, full.Detail));
        scenarios.Add(PassIf(50, "Row counts and semantic hashes remain stable", full.Completed && full.RowCountsMatch, full.Detail));
    }

    private static async Task<MigrationRun> RunSuccessfulMigrationAsync(string root, string name, FixtureProfile profile)
    {
        var databasePath = DatabasePath(root, name);
        await FixtureBuilder.CreateAsync(databasePath, profile).ConfigureAwait(false);
        var result = await Service(databasePath, authorized: true).MigrateAsync().ConfigureAwait(false);
        var service = new CashJournalService(new LocalOperationsDatabaseOptions(
            DatabasePath: databasePath,
            CentralPmsBaseUrl: "UNCONFIGURED_CENTRAL_PMS"));
        await service.InitializeAsync().ConfigureAwait(false);
        var readiness = service.GetLocalPersistenceReadiness();
        var state = await service.GetLocalOperationalStateAsync().ConfigureAwait(false);
        var rows = await ReadEncryptedRowsAsync(databasePath).ConfigureAwait(false);
        return new MigrationRun(
            databasePath,
            result.Status == LocalDatabasePlaintextMigrationStatus.MigrationCompleted,
            readiness.PersistenceReady,
            state.ActiveShiftRecordCount,
            state.ActiveCashCustodySessionRecordCount,
            rows,
            result.SourceRowCounts.Count == 0 || result.SourceRowCounts.SequenceEqual(rows),
            rows.GetValueOrDefault("cashier_shifts") <= 2 && rows.GetValueOrDefault("cash_custody_sessions") <= 1,
            await EncryptedPayableBasisContainsAsync(databasePath, "statutory-validation-synthetic").ConfigureAwait(false),
            result.Status.ToString());
    }

    private static async Task<ScenarioResult> ExpectStatusAsync(
        int number,
        string root,
        string name,
        string directory,
        FixtureProfile profile,
        LocalDatabasePlaintextMigrationStatus expected,
        bool authorized = false,
        Func<long>? availableDiskBytes = null,
        Func<bool>? isAppRunning = null)
    {
        var databasePath = DatabasePath(root, directory);
        await FixtureBuilder.CreateAsync(databasePath, profile).ConfigureAwait(false);
        var result = await Service(databasePath, authorized, availableDiskBytes: availableDiskBytes, isAppRunning: isAppRunning).MigrateAsync().ConfigureAwait(false);
        return PassIf(number, name, result.Status == expected, result.Status.ToString());
    }

    private static async Task<ScenarioResult> ExistingEnvelopeConflictAsync(string root)
    {
        var databasePath = DatabasePath(root, "scenario-14-envelope");
        await FixtureBuilder.CreateAsync(databasePath, FixtureProfile.Minimum).ConfigureAwait(false);
        WriteSyntheticValidEnvelope(databasePath);
        var result = await Service(databasePath, authorized: true).MigrateAsync().ConfigureAwait(false);
        return PassIf(14, "Plaintext database plus existing envelope blocks safely", result.Status == LocalDatabasePlaintextMigrationStatus.ExistingEnvelopeConflict, result.Status.ToString());
    }

    private static async Task<ScenarioResult> ExistingTargetConflictAsync(string root)
    {
        var databasePath = DatabasePath(root, "scenario-15-target");
        await FixtureBuilder.CreateAsync(databasePath, FixtureProfile.Minimum).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(TargetPath(databasePath))!);
        await File.WriteAllTextAsync(TargetPath(databasePath), "synthetic target conflict").ConfigureAwait(false);
        var result = await Service(databasePath, authorized: true).MigrateAsync().ConfigureAwait(false);
        return PassIf(15, "Existing encrypted target conflict blocks safely", result.Status == LocalDatabasePlaintextMigrationStatus.ExistingTargetConflict, result.Status.ToString());
    }

    private static async Task<ScenarioResult> ExistingBackupConflictAsync(string root)
    {
        var databasePath = DatabasePath(root, "scenario-16-backup");
        await FixtureBuilder.CreateAsync(databasePath, FixtureProfile.Minimum).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath(databasePath))!);
        await File.WriteAllTextAsync(BackupPath(databasePath), "synthetic backup conflict").ConfigureAwait(false);
        var result = await Service(databasePath, authorized: true).MigrateAsync().ConfigureAwait(false);
        return PassIf(16, "Existing backup conflict is classified safely", result.Status == LocalDatabasePlaintextMigrationStatus.ExistingBackupConflict, result.Status.ToString());
    }

    private static async Task<ScenarioResult> SourceChangeBlocksAsync(string root)
    {
        var databasePath = DatabasePath(root, "scenario-21-source-change");
        await FixtureBuilder.CreateAsync(databasePath, FixtureProfile.Minimum).ConfigureAwait(false);
        var op = "source-change-operation";
        var interrupted = await Service(
                databasePath,
                authorized: true,
                operationId: op,
                faultInjector: new OneShotFaultInjector(op, LocalDatabasePlaintextMigrationPhase.SourceValidated, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase))
            .MigrateAsync()
            .ConfigureAwait(false);
        await FixtureBuilder.AddCommittedWalShiftAsync(databasePath).ConfigureAwait(false);
        var result = await Service(databasePath, authorized: true, operationId: op).MigrateAsync().ConfigureAwait(false);
        return PassIf(21, "Source changes after initial validation block safely", interrupted.Status == LocalDatabasePlaintextMigrationStatus.InterruptedMigration && result.Status == LocalDatabasePlaintextMigrationStatus.BlockedForSupport, result.Status.ToString());
    }

    private static async Task<ScenarioResult> InterruptAndResumeAsync(
        int number,
        string root,
        string name,
        LocalDatabasePlaintextMigrationPhase phase,
        LocalDatabasePlaintextMigrationFaultTiming timing)
    {
        var databasePath = DatabasePath(root, $"scenario-{number:00}-interrupt");
        await FixtureBuilder.CreateAsync(databasePath, FixtureProfile.Full).ConfigureAwait(false);
        var operationId = $"operation-{number:00}";
        var interrupted = await Service(
                databasePath,
                authorized: true,
                operationId: operationId,
                faultInjector: new OneShotFaultInjector(operationId, phase, timing))
            .MigrateAsync()
            .ConfigureAwait(false);
        var resumed = await Service(databasePath, authorized: true, operationId: operationId).MigrateAsync().ConfigureAwait(false);
        var noPlaintextFallback = !File.Exists(databasePath) || !HasPlainSqliteHeader(databasePath) || resumed.Status != LocalDatabasePlaintextMigrationStatus.MigrationCompleted;
        return PassIf(
            number,
            name,
            interrupted.Status == LocalDatabasePlaintextMigrationStatus.InterruptedMigration
            && resumed.Status == LocalDatabasePlaintextMigrationStatus.MigrationCompleted
            && noPlaintextFallback,
            $"{phase}/{timing}/{interrupted.Status}->{resumed.Status}");
    }

    private static async Task<ScenarioResult> RollbackRequiredAndCompletedAsync(int number, string root, string name)
    {
        var databasePath = DatabasePath(root, $"scenario-{number:00}-rollback");
        await FixtureBuilder.CreateAsync(databasePath, FixtureProfile.Full).ConfigureAwait(false);
        var operationId = $"operation-{number:00}";
        var failed = await Service(
                databasePath,
                authorized: true,
                operationId: operationId,
                faultInjector: new OneShotFaultInjector(
                    operationId,
                    LocalDatabasePlaintextMigrationPhase.TargetVerified,
                    LocalDatabasePlaintextMigrationFaultTiming.BeforePhase,
                    LocalDatabasePlaintextMigrationStatus.TargetVerificationFailed,
                    LocalDatabasePlaintextMigrationPhase.RollbackRequired))
            .MigrateAsync()
            .ConfigureAwait(false);
        var rolledBack = await Service(databasePath, rollback: true, operationId: operationId).MigrateAsync().ConfigureAwait(false);
        return PassIf(number, name, failed.Status == LocalDatabasePlaintextMigrationStatus.TargetVerificationFailed && rolledBack.Status == LocalDatabasePlaintextMigrationStatus.RollbackCompleted && HasPlainSqliteHeader(databasePath), $"{failed.Status}->{rolledBack.Status}");
    }

    private static async Task<ScenarioResult> RollbackInterruptionAsync(string root)
    {
        var databasePath = DatabasePath(root, "scenario-33-rollback-interrupt");
        await FixtureBuilder.CreateAsync(databasePath, FixtureProfile.Full).ConfigureAwait(false);
        var operationId = "operation-33";
        var dryRun = await Service(databasePath, authorized: true, dryRun: true, operationId: operationId).MigrateAsync().ConfigureAwait(false);
        var injector = new OneShotFaultInjector(operationId, LocalDatabasePlaintextMigrationPhase.RollbackStarted, LocalDatabasePlaintextMigrationFaultTiming.AfterPhase);
        try
        {
            await Service(databasePath, rollback: true, operationId: operationId, faultInjector: injector).MigrateAsync().ConfigureAwait(false);
        }
        catch (LocalDatabasePlaintextMigrationException)
        {
        }

        var resumed = await Service(databasePath, rollback: true, operationId: operationId).MigrateAsync().ConfigureAwait(false);
        return PassIf(33, "Interrupted rollback resumes safely", dryRun.Status == LocalDatabasePlaintextMigrationStatus.MigrationStarted && resumed.Status == LocalDatabasePlaintextMigrationStatus.RollbackCompleted, resumed.Status.ToString());
    }

    private static LocalDatabasePlaintextMigrationService Service(
        string databasePath,
        bool authorized = false,
        bool dryRun = false,
        bool rollback = false,
        string? operationId = null,
        ILocalDatabasePlaintextMigrationFaultInjector? faultInjector = null,
        Func<long>? availableDiskBytes = null,
        Func<bool>? isAppRunning = null) =>
        new(new LocalDatabasePlaintextMigrationOptions(
            DatabasePath: databasePath,
            Authorized: authorized,
            DryRun: dryRun,
            Rollback: rollback,
            OperationId: operationId,
            FaultInjector: faultInjector,
            UtcNow: () => Now,
            AvailableDiskBytes: availableDiskBytes,
            IsAptApplicationRunning: isAppRunning ?? (() => false)));

    private static ScenarioResult PassIf(int number, string name, bool passed, string detail) =>
        new(number, name, passed ? "PASSED" : "FAILED", detail);

    private static string DatabasePath(string root, string name) =>
        Path.Combine(root, name, "LocalOperations", "cash-journal.db");

    private static string EnvelopePath(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(databasePath)!, LocalDatabaseKeyEnvelope.EnvelopeFileName);

    private static string WorkingPath(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(databasePath)!, "PlaintextMigration");

    private static string StatePath(string databasePath) =>
        Path.Combine(WorkingPath(databasePath), "cash-journal-migration-state.json");

    private static string TargetPath(string databasePath) =>
        Path.Combine(WorkingPath(databasePath), "cash-journal.encrypted-target.db");

    private static string BackupPath(string databasePath) =>
        Path.Combine(WorkingPath(databasePath), "backups", "active-operation", "cash-journal.plaintext.source.backup.db");

    private static bool HasPlainSqliteHeader(string path)
    {
        var expected = Encoding.ASCII.GetBytes("SQLite format 3\0");
        Span<byte> header = stackalloc byte[expected.Length];
        using var stream = File.OpenRead(path);
        return stream.Read(header) == expected.Length && header.SequenceEqual(expected);
    }

    private static bool NoKeyOpenFails(string databasePath)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA schema_version;";
            _ = command.ExecuteScalar();
            return false;
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadEncryptedRowsAsync(string databasePath)
    {
        var manager = new LocalDatabaseEncryptionManager(databasePath);
        await using var connection = manager.OpenEncryptedConnection();
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in FixtureBuilder.RequiredTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
            result[table] = Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false));
        }

        return result;
    }

    private static async Task<bool> EncryptedPayableBasisContainsAsync(string databasePath, string value)
    {
        var manager = new LocalDatabaseEncryptionManager(databasePath);
        await using var connection = manager.OpenEncryptedConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StatutoryDiscountStateJson FROM terminal_cash_payable_basis_states LIMIT 1;";
        var stored = Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false));
        return stored?.Contains(value, StringComparison.Ordinal) == true;
    }

    private static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WriteSyntheticValidEnvelope(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        var manager = new LocalDatabaseEncryptionManager(databasePath);
        var key = LocalDatabaseKeyGenerator.Generate();
        var protector = new DpapiCurrentUserLocalDatabaseKeyProtector();
        var protectedKey = protector.Protect(key, LocalDatabaseKeyEnvelope.EntropyBytes);
        try
        {
            var envelope = LocalDatabaseKeyEnvelope.Create(manager.DatabaseIdentity, protectedKey, Now);
            File.WriteAllText(EnvelopePath(databasePath), envelope.ToJson(), Encoding.UTF8);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // The final report still contains sanitized scenario results if cleanup is blocked by the OS.
        }
    }
}

internal sealed record MigrationRun(
    string DatabasePath,
    bool Completed,
    bool PersistenceReady,
    int ActiveShiftCount,
    int ActiveCustodyCount,
    IReadOnlyDictionary<string, long> TargetRows,
    bool RowCountsMatch,
    bool NoDuplicateRows,
    bool StatutoryStatePreserved,
    string Detail);

internal enum FixtureProfile
{
    Minimum,
    Full,
    Wal,
    WalAndShm,
    Unsupported,
    CorruptHeader,
    ForeignKeyFailure
}

internal sealed class OneShotFaultInjector(
    string operationId,
    LocalDatabasePlaintextMigrationPhase phase,
    LocalDatabasePlaintextMigrationFaultTiming timing,
    LocalDatabasePlaintextMigrationStatus status = LocalDatabasePlaintextMigrationStatus.InterruptedMigration,
    LocalDatabasePlaintextMigrationPhase resultPhase = LocalDatabasePlaintextMigrationPhase.NotStarted) : ILocalDatabasePlaintextMigrationFaultInjector
{
    private bool _fired;

    public ValueTask OnPhaseAsync(
        string currentOperationId,
        LocalDatabasePlaintextMigrationPhase currentPhase,
        LocalDatabasePlaintextMigrationFaultTiming currentTiming,
        CancellationToken cancellationToken)
    {
        if (_fired ||
            !string.Equals(operationId, currentOperationId, StringComparison.Ordinal) ||
            phase != currentPhase ||
            timing != currentTiming)
        {
            return ValueTask.CompletedTask;
        }

        _fired = true;
        throw new LocalDatabasePlaintextMigrationException(
            status,
            resultPhase == LocalDatabasePlaintextMigrationPhase.NotStarted ? phase : resultPhase,
            "Synthetic validation-only migration fault.",
            "Rerun the deterministic validation operation.");
    }
}

internal static class FixtureBuilder
{
    public static readonly string[] RequiredTables =
    [
        "cashier_shifts",
        "cash_custody_sessions",
        "cash_tenders",
        "cash_tender_events",
        "cash_denomination_entries",
        "terminal_cash_payment_outbox_commands",
        "terminal_cash_payment_submission_attempts",
        "terminal_cash_fiscal_outbox_commands",
        "terminal_cash_fiscal_attempts",
        "terminal_cash_receipt_retrieval_commands",
        "terminal_cash_receipt_retrieval_attempts",
        "terminal_cash_receipt_print_jobs",
        "terminal_cash_payable_basis_states"
    ];

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-30T00:00:00Z");

    public static async Task CreateAsync(string databasePath, FixtureProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        if (profile == FixtureProfile.Unsupported)
        {
            await using var unsupported = await OpenAsync(databasePath, create: true).ConfigureAwait(false);
            await ExecuteAsync(unsupported, "CREATE TABLE unsupported_values (Value TEXT NOT NULL);").ConfigureAwait(false);
            return;
        }

        if (profile == FixtureProfile.CorruptHeader)
        {
            await File.WriteAllBytesAsync(databasePath, Encoding.ASCII.GetBytes("SQLite format 3\0corrupt synthetic bytes")).ConfigureAwait(false);
            return;
        }

        SQLitePCL.Batteries_V2.Init();
        await using var connection = await OpenAsync(databasePath, create: true).ConfigureAwait(false);
        await using var dbContext = CreateDbContext(connection);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
        AddBaseRows(dbContext, includeCustody: profile is FixtureProfile.Full or FixtureProfile.Wal or FixtureProfile.WalAndShm);
        if (profile is FixtureProfile.Full or FixtureProfile.Wal or FixtureProfile.WalAndShm)
        {
            AddRecoveryRows(dbContext);
        }

        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        if (profile is FixtureProfile.Wal or FixtureProfile.WalAndShm)
        {
            await AddCommittedWalShiftAsync(databasePath).ConfigureAwait(false);
        }

        if (profile == FixtureProfile.ForeignKeyFailure)
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;").ConfigureAwait(false);
            await ExecuteAsync(connection, """
                INSERT INTO cash_tenders (
                    Id, CashCustodySessionId, ParkingSessionId, TariffSnapshotId, Currency, AmountDue, AmountTendered, ChangeDue,
                    CorrelationId, LocalIdempotencyIdentity, CurrentLocalState, CreatedAt, UpdatedAt
                ) VALUES (
                    '99999999-9999-4999-8999-999999999999', '99999999-9999-4999-8999-999999999998',
                    'parking-invalid', 'tariff-invalid', 'PHP', 1, 1, 0, 'corr-invalid', 'idem-invalid',
                    'TenderStarted', 638895744000000000, 638895744000000000
                );
                """).ConfigureAwait(false);
        }
    }

    public static async Task AddCommittedWalShiftAsync(string databasePath)
    {
        await using var connection = await OpenAsync(databasePath).ConfigureAwait(false);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;").ConfigureAwait(false);
        await ExecuteAsync(connection, """
            INSERT INTO cashier_shifts (
                Id, CashierId, AuthenticatedCashierSessionReference, TerminalId, SiteId, SiteGroupId, PosServerId, OpenedAt, ClosedAt, Status
            ) VALUES (
                'shift-wal-synthetic', 'cashier-synthetic', 'auth-session-synthetic', 'terminal-synthetic',
                'site-synthetic', 'site-group-synthetic', 'pos-synthetic', 638895744000000000, NULL, 'Open'
            );
            """).ConfigureAwait(false);
    }

    private static void AddBaseRows(CashJournalDbContext dbContext, bool includeCustody)
    {
        dbContext.CashierShifts.Add(new CashierShift
        {
            Id = "shift-synthetic-open",
            CashierId = "cashier-synthetic",
            AuthenticatedCashierSessionReference = "auth-session-synthetic",
            TerminalId = "terminal-synthetic",
            SiteId = "site-synthetic",
            SiteGroupId = "site-group-synthetic",
            PosServerId = "pos-synthetic",
            OpenedAt = Now,
            Status = CashierShiftStatus.Open
        });

        if (!includeCustody)
        {
            return;
        }

        dbContext.CashCustodySessions.Add(new CashCustodySession
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa0001"),
            CashierId = "cashier-synthetic",
            AuthenticatedCashierSessionReference = "auth-session-synthetic",
            CashierShiftId = "shift-synthetic-open",
            TerminalId = "terminal-synthetic",
            SiteId = "site-synthetic",
            SiteGroupId = "site-group-synthetic",
            PosServerId = "pos-synthetic",
            OpeningCashAmount = 1000m,
            OpenedAt = Now,
            Status = CashCustodySessionStatus.Open
        });
    }

    private static void AddRecoveryRows(CashJournalDbContext dbContext)
    {
        var custodyId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaa0001");
        var tenderId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbb0001");
        var eventId = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccc0001");
        var paymentCommandId = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddd0001");
        var fiscalCommandId = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeee0001");
        var receiptCommandId = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffff0001");

        dbContext.CashTenders.Add(new CashTender
        {
            Id = tenderId,
            CashCustodySessionId = custodyId,
            ParkingSessionId = "parking-synthetic",
            TariffSnapshotId = "tariff-synthetic",
            Currency = "PHP",
            AmountDue = 125m,
            AmountTendered = 125m,
            ChangeDue = 0m,
            CorrelationId = "corr-synthetic",
            LocalIdempotencyIdentity = "idem-synthetic",
            CurrentLocalState = CashTenderState.CashReceived,
            StatutoryDiscountDecisionCommandId = "decision-synthetic",
            StatutoryDiscountPayableBasisApplicationCommandId = "application-synthetic",
            StatutoryDiscountValidationId = "statutory-validation-synthetic",
            StatutoryAppliedTariffSnapshotId = "tariff-synthetic",
            StatutoryFinalAmountMinorUnits = 12500,
            StatutoryCurrency = "PHP",
            CreatedAt = Now,
            UpdatedAt = Now
        });
        dbContext.CashTenderEvents.Add(new CashTenderEvent
        {
            Id = eventId,
            CashTenderId = tenderId,
            EventType = CashTenderEventType.CashReceived,
            OccurredAt = Now,
            AmountTendered = 125m,
            ChangeDue = 0m,
            CashierAttested = true,
            ActorCashierId = "cashier-synthetic",
            CorrelationId = "corr-synthetic"
        });
        dbContext.CashDenominationEntries.AddRange(
            new CashDenominationEntry
            {
                Id = Guid.Parse("11111111-1111-4111-8111-111111111111"),
                CashTenderEventId = eventId,
                DenominationCode = "PHP-100",
                DenominationValue = 100m,
                Quantity = 1,
                CreatedAt = Now
            },
            new CashDenominationEntry
            {
                Id = Guid.Parse("22222222-2222-4222-8222-222222222222"),
                CashTenderEventId = eventId,
                DenominationCode = "PHP-20",
                DenominationValue = 20m,
                Quantity = 1,
                CreatedAt = Now
            });

        dbContext.TerminalCashPaymentOutboxCommands.Add(new TerminalCashPaymentOutboxCommand
        {
            Id = paymentCommandId,
            TerminalCashTenderId = tenderId,
            CashCustodySessionId = custodyId,
            RequestPayloadJson = "{\"synthetic\":true}",
            RequestPayloadHash = "hash-payment-synthetic",
            IdempotencyKey = "idempotency-payment-synthetic",
            OriginalCorrelationId = "corr-payment-synthetic",
            CentralPmsTarget = "https://central-pms.example.invalid",
            Status = TerminalCashPaymentCommandStatus.Confirmed,
            AttemptCount = 1,
            CanonicalPaymentAttemptId = Guid.Parse("33333333-3333-4333-8333-333333333333"),
            CanonicalPaymentConfirmationId = Guid.Parse("44444444-4444-4444-8444-444444444444"),
            ConfirmedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        dbContext.TerminalCashPaymentSubmissionAttempts.Add(new TerminalCashPaymentSubmissionAttempt
        {
            Id = Guid.Parse("55555555-5555-4555-8555-555555555555"),
            LocalCommandId = paymentCommandId,
            OperationType = TerminalCashPaymentOutboxOperationType.Submit,
            AttemptSequence = 1,
            StartedAt = Now,
            CompletedAt = Now,
            OutcomeClassification = TerminalCashPaymentAttemptOutcome.Confirmed,
            CorrelationId = "corr-payment-attempt-synthetic"
        });

        dbContext.TerminalCashFiscalOutboxCommands.Add(new TerminalCashFiscalOutboxCommand
        {
            Id = fiscalCommandId,
            TerminalCashTenderId = tenderId,
            RelatedCashPaymentOutboxCommandId = paymentCommandId,
            CanonicalPaymentAttemptId = Guid.Parse("33333333-3333-4333-8333-333333333333"),
            CanonicalPaymentConfirmationId = Guid.Parse("44444444-4444-4444-8444-444444444444"),
            RequestRepresentationJson = "{\"synthetic\":true}",
            RequestHash = "hash-fiscal-synthetic",
            FiscalIdempotencyKey = "idempotency-fiscal-synthetic",
            FiscalCorrelationId = "corr-fiscal-synthetic",
            CentralPmsTarget = "https://central-pms.example.invalid",
            Status = TerminalCashFiscalCommandStatus.Recorded,
            AttemptCount = 1,
            FiscalIssuanceReferenceId = Guid.Parse("66666666-6666-4666-8666-666666666666"),
            FiscalIssuanceState = "FISCAL_ISSUANCE_RECORDED",
            PosFiscalDocumentId = Guid.Parse("77777777-7777-4777-8777-777777777777"),
            FiscalDocumentNumber = "SI-SYNTHETIC-001",
            FiscalNumberAssignedAt = Now,
            SemanticHashSourceVersion = "synthetic-v1",
            RecordedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        dbContext.TerminalCashFiscalAttempts.Add(new TerminalCashFiscalAttempt
        {
            Id = Guid.Parse("88888888-8888-4888-8888-888888888888"),
            LocalFiscalCommandId = fiscalCommandId,
            OperationType = TerminalCashFiscalOperationType.Submit,
            AttemptSequence = 1,
            StartedAt = Now,
            CompletedAt = Now,
            OutcomeClassification = TerminalCashFiscalAttemptOutcome.Recorded,
            CorrelationId = "corr-fiscal-attempt-synthetic"
        });

        dbContext.TerminalCashReceiptRetrievalCommands.Add(new TerminalCashReceiptRetrievalCommand
        {
            Id = receiptCommandId,
            TerminalCashTenderId = tenderId,
            RelatedCashPaymentOutboxCommandId = paymentCommandId,
            RelatedFiscalCommandId = fiscalCommandId,
            CanonicalPaymentAttemptId = Guid.Parse("33333333-3333-4333-8333-333333333333"),
            CanonicalPaymentConfirmationId = Guid.Parse("44444444-4444-4444-8444-444444444444"),
            CanonicalPaymentStatus = "CONFIRMED",
            FiscalIssuanceReferenceId = Guid.Parse("66666666-6666-4666-8666-666666666666"),
            PosFiscalDocumentId = Guid.Parse("77777777-7777-4777-8777-777777777777"),
            RetrievalCorrelationId = "corr-receipt-synthetic",
            CentralPmsTarget = "https://central-pms.example.invalid",
            Status = TerminalCashReceiptRetrievalStatus.Available,
            AttemptCount = 1,
            FiscalDocumentNumber = "SI-SYNTHETIC-001",
            FiscalDocumentStatus = "RECORDED",
            PresentationVersion = "synthetic-presentation-v1",
            TemplateVersion = "synthetic-template-v1",
            ContentType = "application/json",
            AuthoritativePresentationJson = "{\"synthetic\":true}",
            AuthoritativePayloadHash = "hash-receipt-synthetic",
            RetrievedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        dbContext.TerminalCashReceiptRetrievalAttempts.Add(new TerminalCashReceiptRetrievalAttempt
        {
            Id = Guid.Parse("99999999-9999-4999-8999-999999999999"),
            LocalReceiptRetrievalId = receiptCommandId,
            AttemptSequence = 1,
            StartedAt = Now,
            CompletedAt = Now,
            OutcomeClassification = TerminalCashReceiptRetrievalAttemptOutcome.Available,
            CorrelationId = "corr-receipt-attempt-synthetic"
        });
        dbContext.TerminalCashReceiptPrintJobs.Add(new TerminalCashReceiptPrintJob
        {
            Id = Guid.Parse("12121212-1212-4121-8121-121212121212"),
            TerminalCashTenderId = tenderId,
            LocalReceiptRetrievalId = receiptCommandId,
            FiscalIssuanceReferenceId = Guid.Parse("66666666-6666-4666-8666-666666666666"),
            PosFiscalDocumentId = Guid.Parse("77777777-7777-4777-8777-777777777777"),
            FiscalDocumentNumber = "SI-SYNTHETIC-001",
            PresentationVersion = "synthetic-presentation-v1",
            TemplateVersion = "synthetic-template-v1",
            AuthoritativePayloadHash = "hash-receipt-synthetic",
            PaperWidthMm = 80,
            PaperProfileId = "paper-synthetic",
            ConfiguredPrinterName = "printer-synthetic",
            Classification = TerminalCashReceiptPrintClassification.Original,
            CopySequence = 1,
            Status = TerminalCashReceiptPrintJobStatus.Completed,
            RequestedAt = Now,
            RequestedBy = "cashier-synthetic",
            CompletedAt = Now,
            LastUpdatedAt = Now,
            CorrelationId = "corr-print-synthetic"
        });
        dbContext.TerminalCashPayableBasisStates.Add(new TerminalCashPayableBasisState
        {
            Id = Guid.Parse("13131313-1313-4131-8131-131313131313"),
            LocalWorkflowId = "workflow-synthetic",
            LookupReferenceType = "ticket",
            LookupReferenceValue = "TICKET-SYNTHETIC",
            ParkingSessionId = "parking-synthetic",
            TariffSnapshotId = "tariff-synthetic",
            SiteId = "site-synthetic",
            SiteGroupId = "site-group-synthetic",
            SitePosServerId = "pos-synthetic",
            TerminalId = "terminal-synthetic",
            AuthoritativeAmountMinorUnits = 12500,
            Currency = "PHP",
            TariffValidUntil = Now.AddMinutes(30),
            ParkingStatus = "ACTIVE",
            PaymentStatus = "UNPAID",
            ReadyForCashAcceptance = false,
            BlockingReasonCodesJson = "[]",
            Retryable = false,
            SafeUserFacingClassification = "Ready",
            CentralPmsCorrelationId = "corr-basis-synthetic",
            StatutoryDiscountStateJson = "{\"statutoryValidationId\":\"statutory-validation-synthetic\"}",
            ResolvedAt = Now,
            UpdatedAt = Now
        });
    }

    private static async Task<SqliteConnection> OpenAsync(string path, bool create = false)
    {
        SQLitePCL.Batteries_V2.Init();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static CashJournalDbContext CreateDbContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<CashJournalDbContext>()
            .UseSqlite(connection, contextOwnsConnection: false)
            .Options;
        return new CashJournalDbContext(options);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
