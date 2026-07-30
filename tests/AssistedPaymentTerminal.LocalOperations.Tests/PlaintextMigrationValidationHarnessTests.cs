using AssistedPaymentTerminal.PlaintextDatabaseMigration.ValidationHarness;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;

namespace AssistedPaymentTerminal.LocalOperations.Tests;

public sealed class PlaintextMigrationValidationHarnessTests
{
    [Fact]
    public void NormalizeLocalAccountName_RewritesDotLocalAccountToMachineQualifiedName()
    {
        var normalized = WrongUserDpapiProof.NormalizeLocalAccountName(@".\ExitPassJ003Proof");

        Assert.Equal(string.Concat(Environment.MachineName, @"\ExitPassJ003Proof"), normalized);
    }

    [Fact]
    public void NormalizeLocalAccountName_RewritesBareLocalAccountToMachineQualifiedName()
    {
        var normalized = WrongUserDpapiProof.NormalizeLocalAccountName("ExitPassJ003Proof");

        Assert.Equal(string.Concat(Environment.MachineName, @"\ExitPassJ003Proof"), normalized);
    }

    [Fact]
    public void NormalizeLocalAccountName_PreservesExplicitDomainAccountName()
    {
        var normalized = WrongUserDpapiProof.NormalizeLocalAccountName(@"DOMAIN\ExitPassJ003Proof");

        Assert.Equal(@"DOMAIN\ExitPassJ003Proof", normalized);
    }

    [Fact]
    public void SidAclRules_GrantLeastPrivilegeWithoutBroadGroups()
    {
        const string sid = "S-1-5-21-1000-1000-1000-1001";

        var proofRule = WrongUserDpapiProof.CreateProofDirectoryReadRule(sid);
        var resultRule = WrongUserDpapiProof.CreateResultDirectoryWriteRule(sid);

        Assert.Equal(string.Concat("*", sid, ":(OI)(CI)RX"), proofRule);
        Assert.Equal(string.Concat("*", sid, ":(OI)(CI)M"), resultRule);
        Assert.DoesNotContain("Everyone", proofRule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", proofRule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authenticated Users", proofRule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Everyone", resultRule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", resultRule, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authenticated Users", resultRule, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityReferenceMatchesSid_MatchesSecurityIdentifier()
    {
        var sid = WindowsIdentity.GetCurrent().User!;

        Assert.True(WrongUserDpapiProof.IdentityReferenceMatchesSid(sid, sid.Value));
    }

    [Fact]
    public void IdentityReferenceMatchesSid_MatchesTranslatedNtAccount()
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        var account = (NTAccount)sid.Translate(typeof(NTAccount));

        Assert.True(WrongUserDpapiProof.IdentityReferenceMatchesSid(account, sid.Value));
    }

    [Fact]
    public void RightsInclude_AcceptsRightsSuperset()
    {
        Assert.True(WrongUserDpapiProof.RightsInclude(FileSystemRights.FullControl, FileSystemRights.Modify));
    }

    [Fact]
    public void GrantAlternateUserAccess_AppliesAndVerifiesCurrentUserAcl()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExitPassHarnessAclTests", Guid.NewGuid().ToString("N"));
        var proof = Path.Combine(root, "proof");
        var result = Path.Combine(proof, "AlternateUserResult");
        try
        {
            Directory.CreateDirectory(result);
            File.WriteAllText(Path.Combine(proof, "wrong-user-proof-manifest.json"), "{}");
            File.WriteAllText(Path.Combine(proof, "cash-journal.key"), "{}");
            File.WriteAllText(Path.Combine(proof, "cash-journal.db"), "synthetic");

            var aclResult = WrongUserDpapiProof.GrantAlternateUserAccess(
                proof,
                result,
                WindowsIdentity.GetCurrent().Name);

            Assert.True(aclResult.Succeeded, aclResult.SafeMessage);
            Assert.Equal("AclReady", aclResult.Classification);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void VerifyAlternateUserAccess_ReturnsMissingAceForUnrelatedSid()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExitPassHarnessAclTests", Guid.NewGuid().ToString("N"));
        var proof = Path.Combine(root, "proof");
        var result = Path.Combine(proof, "AlternateUserResult");
        try
        {
            Directory.CreateDirectory(result);
            File.WriteAllText(Path.Combine(proof, "wrong-user-proof-manifest.json"), "{}");

            var verification = WrongUserDpapiProof.VerifyAlternateUserAccess(
                proof,
                result,
                "S-1-5-21-1111111111-2222222222-3333333333-4444");

            Assert.False(verification.Succeeded);
            Assert.Equal("ProofDirectoryAceMissing", verification.Classification);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void VerifyAlternateUserAccess_ReturnsInsufficientRightsWhenSidCannotReadProofDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExitPassHarnessAclTests", Guid.NewGuid().ToString("N"));
        var proof = Path.Combine(root, "proof");
        var result = Path.Combine(proof, "AlternateUserResult");
        var sid = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-4445");
        try
        {
            Directory.CreateDirectory(result);
            AddDirectoryRule(proof, sid, FileSystemRights.ReadAttributes, AccessControlType.Allow);

            var verification = WrongUserDpapiProof.VerifyAlternateUserAccess(proof, result, sid.Value);

            Assert.False(verification.Succeeded);
            Assert.Equal("ProofDirectoryRightsInsufficient", verification.Classification);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void VerifyAlternateUserAccess_ReturnsConflictingDenyRule()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExitPassHarnessAclTests", Guid.NewGuid().ToString("N"));
        var proof = Path.Combine(root, "proof");
        var result = Path.Combine(proof, "AlternateUserResult");
        var sid = new SecurityIdentifier("S-1-5-21-1111111111-2222222222-3333333333-4446");
        try
        {
            Directory.CreateDirectory(result);
            AddDirectoryRule(proof, sid, FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory, AccessControlType.Allow);
            AddDirectoryRule(proof, sid, FileSystemRights.Read, AccessControlType.Deny);

            var verification = WrongUserDpapiProof.VerifyAlternateUserAccess(proof, result, sid.Value);

            Assert.False(verification.Succeeded);
            Assert.Equal("ConflictingDenyRule", verification.Classification);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HarnessAclVerification_DoesNotDependOnIcaclsDisplayText()
    {
        var repository = RepositoryPath.Find();
        var harness = Path.Combine(
            repository,
            "tools",
            "AssistedPaymentTerminal.PlaintextDatabaseMigration.ValidationHarness",
            "Program.cs");

        var source = await File.ReadAllTextAsync(harness);

        Assert.DoesNotContain("AclContainsSid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Stdout.Contains", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAlternateUserSid_MissingLocalAccountReturnsSafeClassification()
    {
        var result = WrongUserDpapiProof.ResolveAlternateUserSid(string.Concat("ExitPassMissingProofUser-", Guid.NewGuid().ToString("N")));

        Assert.False(result.Succeeded);
        Assert.Equal("AlternateAccountNotFound", result.Classification);
        Assert.Null(result.Sid);
        Assert.DoesNotContain("Exception", result.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PrepareAsync_ManifestAndEnvelopeDoNotContainPlaintextKeyOrSecretFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "ExitPassHarnessTests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new HarnessOptions(
                true,
                root,
                "All",
                true,
                "prepare-wrong-user-proof",
                null);

            var result = await WrongUserDpapiProof.PrepareAsync(options);

            Assert.Equal("PREPARED", result.Result);
            Assert.True(File.Exists(result.DatabasePath));
            Assert.True(File.Exists(result.EnvelopePath));

            var manifestPath = Path.Combine(root, "WrongUserDpapiProof", "wrong-user-proof-manifest.json");
            var manifest = await File.ReadAllTextAsync(manifestPath);
            var envelope = await File.ReadAllTextAsync(result.EnvelopePath!);

            Assert.DoesNotContain("connection string", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(("Pass" + "word="), manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(("PRAGMA " + "key"), manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SQLite format 3", envelope, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(("Pass" + "word="), envelope, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            WrongUserDpapiProof.Cleanup(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProductionCli_DoesNotExposeWrongUserProofCommands()
    {
        var repository = RepositoryPath.Find();
        var productionCli = Path.Combine(
            repository,
            "tools",
            "AssistedPaymentTerminal.PlaintextDatabaseMigration",
            "Program.cs");

        var source = await File.ReadAllTextAsync(productionCli);

        Assert.DoesNotContain("prepare-wrong-user-proof", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verify-wrong-user-proof", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cleanup-wrong-user-proof", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fail-after-phase", source, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddDirectoryRule(
        string path,
        SecurityIdentifier sid,
        FileSystemRights rights,
        AccessControlType type)
    {
        var directory = new DirectoryInfo(path);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            type));
        directory.SetAccessControl(security);
    }
}
