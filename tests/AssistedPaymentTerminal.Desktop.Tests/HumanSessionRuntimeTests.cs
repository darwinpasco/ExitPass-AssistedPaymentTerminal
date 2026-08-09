using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AssistedPaymentTerminal.Desktop;
using AssistedPaymentTerminal.LocalOperations;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class HumanSessionRuntimeTests : IDisposable
{
    private static readonly Guid SiteId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid SiteGroupId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid DeviceId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid CashierId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly string[] AllOperationalPermissions =
    [
        HumanSessionRuntimeOptions.AccessPermission,
        HumanSessionRuntimeOptions.ShiftOperatePermission,
        HumanSessionRuntimeOptions.CustodyOperatePermission,
        HumanSessionRuntimeOptions.CashReceivePermission
    ];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ExitPass.APT.HumanSession.Tests", Guid.NewGuid().ToString("N"));

    public HumanSessionRuntimeTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task LoginOpensOnlyOwnShiftAndCustodyAndBlocksLogoutWhileCustodyIsOpen()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var store = new MemoryCredentialStore();
        var runtime = CreateRuntime(client, store, out _);

        var login = await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
        var shift = await runtime.OpenOrResumeShiftAsync();
        var custody = await runtime.OpenOrResumeCustodyAsync(500m);
        var authorization = await runtime.AuthorizeCashAsync();
        var logout = await runtime.LogoutAsync();
        var refreshed = await runtime.RefreshAsync();

        Assert.True(login.Authenticated);
        Assert.Equal("APT", login.Audience);
        Assert.Equal("PASSWORD", login.Assurance);
        Assert.False(login.MfaRequired);
        Assert.True(login.ShiftOperationsAuthorized);
        Assert.True(login.CustodyOperationsAuthorized);
        Assert.True(login.CashOperationsAuthorized);
        Assert.NotNull(shift.ActiveShift);
        Assert.Equal(CashierId.ToString("D"), shift.ActiveShift!.CashierId);
        Assert.NotNull(custody.ActiveCashCustodySession);
        Assert.Equal(CashierId.ToString("D"), custody.ActiveCashCustodySession!.CashierId);
        Assert.True(authorization.Authorized);
        Assert.Equal("OPEN_CUSTODY_LOGOUT_BLOCKED", logout.ErrorCode);
        Assert.Contains("Sign out is unavailable while you have open cash custody.", logout.SafeMessage, StringComparison.Ordinal);
        Assert.True(logout.Authenticated);
        Assert.Equal("OPEN_CUSTODY_LOGOUT_BLOCKED", refreshed.ErrorCode);
        Assert.Contains("Sign out is unavailable while you have open cash custody.", refreshed.SafeMessage, StringComparison.Ordinal);
        Assert.NotNull(store.Credential);
    }

    [Fact]
    public async Task AnotherCashierCannotInheritOpenShiftOrCustody()
    {
        var journal = CreateJournal();
        var otherCashier = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var shift = await journal.OpenCashierShiftAsync(OpenShift(otherCashier, "SHIFT-OTHER"));
        Assert.True(shift.IsSuccess);
        var custody = await journal.CreateCashCustodySessionAsync(CreateCustody(otherCashier, "SHIFT-OTHER"));
        Assert.True(custody.IsSuccess);

        var runtime = CreateRuntime(new FakeHumanSessionClient(Success(CashierId)), new MemoryCredentialStore(), journal);
        var login = await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
        var attemptedShift = await runtime.OpenOrResumeShiftAsync();
        var attemptedCustody = await runtime.OpenOrResumeCustodyAsync(0m);

        Assert.False(login.CashOperationsAuthorized);
        Assert.Equal("CROSS_CASHIER_CUSTODY_BLOCKED", login.ErrorCode);
        Assert.Null(attemptedShift.ActiveShift);
        Assert.Null(attemptedCustody.ActiveCashCustodySession);
        Assert.Contains("inherit", attemptedCustody.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SESSION_EXPIRED")]
    [InlineData("SESSION_REVOKED")]
    public async Task ExpiryOrRevocationPreservesCustodyAndBlocksNewCash(string errorCode)
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var store = new MemoryCredentialStore();
        var runtime = CreateRuntime(client, store, out var journal);
        await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
        await runtime.OpenOrResumeShiftAsync();
        await runtime.OpenOrResumeCustodyAsync(100m);
        client.GetResult = Failure(errorCode);

        var authorization = await runtime.AuthorizeCashAsync();
        var durable = await journal.GetLocalOperationalStateAsync(new LocalOperationalStateRequest(
            CashierId: CashierId.ToString("D"),
            TerminalId: "APT-TERMINAL-001",
            SiteId: SiteId.ToString("D"),
            SiteGroupId: SiteGroupId.ToString("D"),
            PosServerId: "POS-001"));

        Assert.False(authorization.Authorized);
        Assert.Equal(errorCode, authorization.Code);
        Assert.NotNull(durable.ActiveCashCustodySession);
        Assert.Equal(CashCustodySessionStatus.Open, durable.ActiveCashCustodySession!.Status);
        Assert.Null(store.Credential);
        Assert.Equal(1, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
    }

    [Theory]
    [InlineData("SESSION_REVOKED")]
    [InlineData("SESSION_EXPIRED")]
    public async Task InvalidatedSessionNeverReplaysPasswordAndExplicitSignInRecoversOwnAccountability(string errorCode)
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var store = new MemoryCredentialStore();
        var runtime = CreateRuntime(client, store, out var journal);
        await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
        await runtime.OpenOrResumeShiftAsync();
        await runtime.OpenOrResumeCustodyAsync(100m);
        client.GetResult = Failure(errorCode);

        var locked = await runtime.RefreshAsync();
        var secondRefresh = await runtime.RefreshAsync();
        var durable = await journal.GetLocalOperationalStateAsync(new LocalOperationalStateRequest(
            CashierId: CashierId.ToString("D"),
            TerminalId: "APT-TERMINAL-001",
            SiteId: SiteId.ToString("D"),
            SiteGroupId: SiteGroupId.ToString("D"),
            PosServerId: "POS-001"));

        Assert.False(locked.Authenticated);
        Assert.Equal("LOCKED", locked.AuthenticationState);
        Assert.False(locked.ShiftOperationsAuthorized);
        Assert.False(locked.CustodyOperationsAuthorized);
        Assert.False(locked.CashOperationsAuthorized);
        Assert.NotNull(locked.ActiveShift);
        Assert.NotNull(locked.ActiveCashCustodySession);
        Assert.False(secondRefresh.Authenticated);
        Assert.Equal("LOCKED", secondRefresh.AuthenticationState);
        Assert.NotNull(secondRefresh.ActiveShift);
        Assert.NotNull(secondRefresh.ActiveCashCustodySession);
        Assert.Equal(1, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
        Assert.Null(store.Credential);
        Assert.NotNull(durable.ActiveShift);
        Assert.NotNull(durable.ActiveCashCustodySession);

        client.LoginResult = Success(CashierId);
        client.GetResult = client.LoginResult;
        var recovered = await runtime.LoginAsync("cashier.synthetic", Credential(runtime, credentialValue: "freshly-entered-password"));

        Assert.True(recovered.Authenticated);
        Assert.NotNull(recovered.ActiveShift);
        Assert.NotNull(recovered.ActiveCashCustodySession);
        Assert.Equal(2, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
    }

    [Fact]
    public async Task RestartUsesOnlineContinuationAndCachedCredentialAloneCannotAuthorize()
    {
        var credential = new HumanSessionCredential(Guid.NewGuid(), "opaque-session-token");
        var client = new FakeHumanSessionClient(Success(CashierId));
        var store = new MemoryCredentialStore { Credential = credential };
        var runtime = CreateRuntime(client, store, out _);

        client.ContinueResult = Failure("CENTRAL_PMS_UNAVAILABLE", retryable: true);
        var unavailable = await runtime.RestoreAsync();
        Assert.False(unavailable.Authenticated);
        Assert.False(unavailable.CashOperationsAuthorized);
        Assert.Equal(0, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);

        store.Credential = credential;
        client.ContinueResult = Success(CashierId, sessionReference: credential.SessionReference, token: "rotated-token");
        var restored = await CreateRuntime(client, store, out _).RestoreAsync();

        Assert.True(restored.Authenticated);
        Assert.Equal("rotated-token", store.Credential!.SessionToken);
        Assert.Equal(2, client.ContinueCalls);
        Assert.Equal(0, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
    }

    [Theory]
    [InlineData("SESSION_REVOKED", false)]
    [InlineData("SESSION_EXPIRED", false)]
    [InlineData("SESSION_INVALID", false)]
    [InlineData("CENTRAL_PMS_UNAVAILABLE", true)]
    public async Task RestartContinuationFailureNeverFallsBackToPasswordAuthentication(string errorCode, bool retryable)
    {
        var credential = new HumanSessionCredential(Guid.NewGuid(), "opaque-session-token");
        var client = new FakeHumanSessionClient(Success(CashierId))
        {
            ContinueResult = Failure(errorCode, retryable)
        };
        var store = new MemoryCredentialStore { Credential = credential };

        var state = await CreateRuntime(client, store, out _).RestoreAsync();

        Assert.False(state.Authenticated);
        Assert.False(state.CashOperationsAuthorized);
        Assert.Equal(1, client.ContinueCalls);
        Assert.Equal(0, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
    }

    [Fact]
    public async Task WrongAudienceDeviceScopeOrPermissionFailsClosed()
    {
        foreach (var response in new[]
        {
            Success(CashierId, audience: "OPERATOR_CONSOLE"),
            Success(CashierId, deviceId: Guid.NewGuid()),
            Success(CashierId, siteIds: [Guid.NewGuid()], siteGroupIds: [Guid.NewGuid()]),
            Success(CashierId, permissions: ["statutory-discounts.policy.resolve"])
        })
        {
            var store = new MemoryCredentialStore();
            var runtime = CreateRuntime(new FakeHumanSessionClient(response), store, out _);
            var state = await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
            Assert.False(state.Authenticated);
            Assert.False(state.CashOperationsAuthorized);
            Assert.Null(store.Credential);
        }
    }

    [Fact]
    public async Task AptAccessPermissionIsRequiredAfterSuccessfulAuthentication()
    {
        var store = new MemoryCredentialStore();
        var runtime = CreateRuntime(
            new FakeHumanSessionClient(Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.AccessPermission))),
            store,
            out _);

        var state = await runtime.LoginAsync("cashier.synthetic", Credential(runtime));

        Assert.False(state.Authenticated);
        Assert.False(state.ShiftOperationsAuthorized);
        Assert.False(state.CustodyOperationsAuthorized);
        Assert.False(state.CashOperationsAuthorized);
        Assert.Equal("APT_ACCESS_PERMISSION_DENIED", state.ErrorCode);
        Assert.Null(store.Credential);
    }

    [Fact]
    public async Task OperationSpecificPermissionsGateOnlyTheirAuthoritativeBoundaries()
    {
        var shiftRuntime = CreateRuntime(
            new FakeHumanSessionClient(Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.ShiftOperatePermission))),
            new MemoryCredentialStore(),
            out _);
        var shiftLogin = await shiftRuntime.LoginAsync("cashier.synthetic", Credential(shiftRuntime));
        var shiftDenied = await shiftRuntime.OpenOrResumeShiftAsync();
        Assert.True(shiftLogin.Authenticated);
        Assert.False(shiftLogin.ShiftOperationsAuthorized);
        Assert.Equal("SHIFT_PERMISSION_DENIED", shiftDenied.ErrorCode);
        Assert.Null(shiftDenied.ActiveShift);

        var custodyRuntime = CreateRuntime(
            new FakeHumanSessionClient(Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.CustodyOperatePermission))),
            new MemoryCredentialStore(),
            out _);
        await custodyRuntime.LoginAsync("cashier.synthetic", Credential(custodyRuntime));
        var shift = await custodyRuntime.OpenOrResumeShiftAsync();
        var custodyDenied = await custodyRuntime.OpenOrResumeCustodyAsync(100m);
        Assert.NotNull(shift.ActiveShift);
        Assert.False(custodyDenied.CustodyOperationsAuthorized);
        Assert.Equal("CUSTODY_PERMISSION_DENIED", custodyDenied.ErrorCode);
        Assert.Null(custodyDenied.ActiveCashCustodySession);

        var receiveRuntime = CreateRuntime(
            new FakeHumanSessionClient(Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.CashReceivePermission))),
            new MemoryCredentialStore(),
            out _);
        await receiveRuntime.LoginAsync("cashier.synthetic", Credential(receiveRuntime));
        await receiveRuntime.OpenOrResumeShiftAsync();
        await receiveRuntime.OpenOrResumeCustodyAsync(100m);
        var receiveDenied = await receiveRuntime.AuthorizeCashAsync();
        Assert.False(receiveDenied.Authorized);
        Assert.Equal("CASH_RECEIVE_PERMISSION_DENIED", receiveDenied.Code);
    }

    [Fact]
    public async Task HumanSessionBridgeReturnsSafeOperationSpecificPermissionDenial()
    {
        var runtime = CreateRuntime(
            new FakeHumanSessionClient(Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.ShiftOperatePermission))),
            new MemoryCredentialStore(),
            out _);
        var handler = new HumanSessionBridgeHandler(runtime, new FakeHumanCredentialPrompt());

        await handler.HandleWebMessageAsync(JsonSerializer.Serialize(new
        {
            source = HumanSessionBridgeCommand.Source,
            command = HumanSessionBridgeCommand.Login,
            correlationId = "corr-login",
            payload = new LoginPayload("cashier.synthetic")
        }));
        var responseText = await handler.HandleWebMessageAsync(JsonSerializer.Serialize(new
        {
            source = HumanSessionBridgeCommand.Source,
            command = HumanSessionBridgeCommand.OpenOrResumeShift,
            correlationId = "corr-shift",
            payload = new { }
        }));

        Assert.NotNull(responseText);
        using var response = JsonDocument.Parse(responseText!);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var payload = response.RootElement.GetProperty("payload");
        Assert.Equal("SHIFT_PERMISSION_DENIED", payload.GetProperty("errorCode").GetString());
        Assert.False(payload.GetProperty("shiftOperationsAuthorized").GetBoolean());
        Assert.DoesNotContain(HumanSessionRuntimeOptions.ShiftOperatePermission, responseText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HumanSessionBridgePublishesOwnedShiftAsOpenAfterOpenResumeAndRefresh()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out _);
        var handler = new HumanSessionBridgeHandler(runtime, new FakeHumanCredentialPrompt());

        await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Login, new LoginPayload("cashier.synthetic"));

        var opened = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.OpenOrResumeShift, new { });
        AssertOwnedOpenShift(opened);

        var resumed = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.OpenOrResumeShift, new { });
        AssertOwnedOpenShift(resumed);

        var refreshed = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Refresh, new { });
        AssertOwnedOpenShift(refreshed);
    }

    [Fact]
    public async Task RestartReconstructsOnlyTheOnlineAuthenticatedCashiersOwnShift()
    {
        var journal = CreateJournal();
        var store = new MemoryCredentialStore();
        var client = new FakeHumanSessionClient(Success(CashierId));
        var firstRuntime = CreateRuntime(client, store, journal);
        await firstRuntime.LoginAsync("cashier.synthetic", Credential(firstRuntime));
        var opened = await firstRuntime.OpenOrResumeShiftAsync();
        Assert.NotNull(opened.ActiveShift);

        client.ContinueResult = Success(CashierId, sessionReference: store.Credential!.SessionReference, token: "rotated-token");
        var sameCashierRestart = await CreateRuntime(client, store, journal).RestoreAsync();
        Assert.NotNull(sameCashierRestart.ActiveShift);
        Assert.Equal(CashierId.ToString("D"), sameCashierRestart.ActiveShift!.CashierId);

        var otherCashier = Guid.Parse("55555555-5555-4555-8555-555555555555");
        var otherRuntime = CreateRuntime(new FakeHumanSessionClient(Success(otherCashier)), new MemoryCredentialStore(), journal);
        var otherLogin = await otherRuntime.LoginAsync("cashier.other", Credential(otherRuntime));
        Assert.Null(otherLogin.ActiveShift);
        Assert.False(otherLogin.CustodyOperationsAuthorized);
        Assert.Equal("CROSS_CASHIER_SHIFT_BLOCKED", otherLogin.ErrorCode);
    }

    [Fact]
    public async Task PayableBasisReadPermissionDoesNotAuthorizeAptOperations()
    {
        var permissions = new[]
        {
            HumanSessionRuntimeOptions.AccessPermission,
            "terminal-cash.payable-basis.read"
        };
        var runtime = CreateRuntime(
            new FakeHumanSessionClient(Success(CashierId, permissions: permissions)),
            new MemoryCredentialStore(),
            out _);

        var login = await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
        var shift = await runtime.OpenOrResumeShiftAsync();
        var custody = await runtime.OpenOrResumeCustodyAsync(100m);
        var cash = await runtime.AuthorizeCashAsync();

        Assert.True(login.Authenticated);
        Assert.False(login.ShiftOperationsAuthorized);
        Assert.False(login.CustodyOperationsAuthorized);
        Assert.False(login.CashOperationsAuthorized);
        Assert.Equal("SHIFT_PERMISSION_DENIED", shift.ErrorCode);
        Assert.Equal("CUSTODY_PERMISSION_DENIED", custody.ErrorCode);
        Assert.False(cash.Authorized);
        Assert.Equal("SHIFT_PERMISSION_DENIED", cash.Code);
    }

    [Fact]
    public async Task CurrentSessionPermissionRevocationBlocksTheNextAffectedOperation()
    {
        var accessClient = new FakeHumanSessionClient(Success(CashierId));
        var accessRuntime = CreateRuntime(accessClient, new MemoryCredentialStore(), out _);
        await accessRuntime.LoginAsync("cashier.synthetic", Credential(accessRuntime));
        accessClient.GetResult = Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.AccessPermission));
        var accessDenied = await accessRuntime.RefreshAsync();
        Assert.False(accessDenied.Authenticated);
        Assert.Equal("APT_ACCESS_PERMISSION_DENIED", accessDenied.ErrorCode);

        var shiftClient = new FakeHumanSessionClient(Success(CashierId));
        var shiftRuntime = CreateRuntime(shiftClient, new MemoryCredentialStore(), out _);
        await shiftRuntime.LoginAsync("cashier.synthetic", Credential(shiftRuntime));
        shiftClient.GetResult = Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.ShiftOperatePermission));
        var shiftDenied = await shiftRuntime.OpenOrResumeShiftAsync();
        Assert.Equal("SHIFT_PERMISSION_DENIED", shiftDenied.ErrorCode);
        Assert.Null(shiftDenied.ActiveShift);

        var custodyClient = new FakeHumanSessionClient(Success(CashierId));
        var custodyRuntime = CreateRuntime(custodyClient, new MemoryCredentialStore(), out _);
        await custodyRuntime.LoginAsync("cashier.synthetic", Credential(custodyRuntime));
        await custodyRuntime.OpenOrResumeShiftAsync();
        custodyClient.GetResult = Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.CustodyOperatePermission));
        var custodyDenied = await custodyRuntime.OpenOrResumeCustodyAsync(100m);
        Assert.Equal("CUSTODY_PERMISSION_DENIED", custodyDenied.ErrorCode);
        Assert.Null(custodyDenied.ActiveCashCustodySession);

        var receiveClient = new FakeHumanSessionClient(Success(CashierId));
        var receiveRuntime = CreateRuntime(receiveClient, new MemoryCredentialStore(), out _);
        await receiveRuntime.LoginAsync("cashier.synthetic", Credential(receiveRuntime));
        await receiveRuntime.OpenOrResumeShiftAsync();
        await receiveRuntime.OpenOrResumeCustodyAsync(100m);
        receiveClient.GetResult = Success(CashierId, permissions: Without(HumanSessionRuntimeOptions.CashReceivePermission));
        var receiveDenied = await receiveRuntime.AuthorizeCashAsync();
        Assert.False(receiveDenied.Authorized);
        Assert.Equal("CASH_RECEIVE_PERMISSION_DENIED", receiveDenied.Code);
    }

    [Fact]
    public async Task ActiveCurrentSessionRefreshRemainsCurrentAndAuthorized()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out _);
        await runtime.LoginAsync("cashier.synthetic", Credential(runtime));

        var refreshed = await runtime.RefreshAsync();

        Assert.True(refreshed.Authenticated);
        Assert.Equal("AUTHENTICATED", refreshed.AuthenticationState);
        Assert.True(refreshed.CashOperationsAuthorized);
        Assert.Equal(1, client.GetCalls);
    }

    [Theory]
    [InlineData("SESSION_REVOKED")]
    [InlineData("SESSION_EXPIRED")]
    [InlineData("SESSION_INVALID")]
    [InlineData("SESSION_NOT_FOUND")]
    public async Task TerminalSessionReadbackClearsAuthorityAndInvalidatesContinuationWhilePreservingAccountability(string errorCode)
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var store = new MemoryCredentialStore();
        var runtime = CreateRuntime(client, store, out _);
        await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
        await runtime.OpenOrResumeShiftAsync();
        await runtime.OpenOrResumeCustodyAsync(100m);
        client.GetResult = Failure(errorCode);

        var locked = await runtime.RefreshAsync();

        Assert.False(locked.Authenticated);
        Assert.Equal("LOCKED", locked.AuthenticationState);
        Assert.False(locked.ShiftOperationsAuthorized);
        Assert.False(locked.CustodyOperationsAuthorized);
        Assert.False(locked.CashOperationsAuthorized);
        Assert.Equal(errorCode, locked.ErrorCode);
        Assert.NotNull(locked.ActiveShift);
        Assert.NotNull(locked.ActiveCashCustodySession);
        Assert.Null(store.Credential);
        Assert.Equal(1, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
    }

    [Fact]
    public async Task CentralPmsUnavailabilityClearsCurrentAuthorityWithoutTreatingContinuationAsValidated()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var store = new MemoryCredentialStore();
        var runtime = CreateRuntime(client, store, out _);
        await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
        await runtime.OpenOrResumeShiftAsync();
        await runtime.OpenOrResumeCustodyAsync(100m);
        client.GetResult = Failure("CENTRAL_PMS_UNAVAILABLE", retryable: true);

        var unavailable = await runtime.RefreshAsync();

        Assert.False(unavailable.Authenticated);
        Assert.Equal("UNAVAILABLE", unavailable.AuthenticationState);
        Assert.False(unavailable.CashOperationsAuthorized);
        Assert.NotNull(unavailable.ActiveShift);
        Assert.NotNull(unavailable.ActiveCashCustodySession);
        Assert.NotNull(store.Credential);
        Assert.Equal(1, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
    }

    [Fact]
    public async Task GlobalScopeWithoutSiteOrSiteGroupAuthorityFailsClosed()
    {
        var runtime = CreateRuntime(
            new FakeHumanSessionClient(Success(CashierId, siteIds: [], siteGroupIds: [], hasGlobalScope: true)),
            new MemoryCredentialStore(),
            out _);

        var state = await runtime.LoginAsync("cashier.synthetic", Credential(runtime));

        Assert.False(state.Authenticated);
        Assert.Equal("SITE_SCOPE_DENIED", state.ErrorCode);
    }

    [Fact]
    public void DpapiCredentialStoreDoesNotPersistPlaintextSessionMaterial()
    {
        var path = Path.Combine(_directory, "human-session.credential");
        var store = new DpapiCurrentUserHumanSessionCredentialStore(path);
        var credential = new HumanSessionCredential(Guid.NewGuid(), "synthetic-secret-session-token");

        store.Save(credential);
        var persisted = File.ReadAllBytes(path);
        var restored = store.Load();

        Assert.NotNull(restored);
        Assert.Equal(credential, restored);
        Assert.DoesNotContain("synthetic-secret-session-token", Encoding.UTF8.GetString(persisted), StringComparison.Ordinal);
        Assert.DoesNotContain(credential.SessionReference.ToString("D"), Encoding.UTF8.GetString(persisted), StringComparison.OrdinalIgnoreCase);
        store.Delete();
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(
            typeof(HumanSessionCredential).GetProperties(BindingFlags.Instance | BindingFlags.Public),
            property => property.Name.Contains("password", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("username", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            typeof(HumanSessionRuntime).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.Name.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BrowserCredentialPayloadAndCancelledNativePromptCannotReachPasswordLogin()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out _);
        var prompt = new FakeHumanCredentialPrompt { Accepted = false };
        var handler = new HumanSessionBridgeHandler(runtime, prompt);

        var legacyPayload = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Login, new Dictionary<string, string>
        {
            ["username"] = "cashier.synthetic",
            ["password"] = "browser-restored-value"
        });
        var cancelledPrompt = await SendBridgeCommandAsync(
            handler,
            HumanSessionBridgeCommand.Login,
            new LoginPayload("cashier.synthetic"));

        Assert.False(legacyPayload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("MALFORMED_REQUEST", legacyPayload.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(cancelledPrompt.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("CREDENTIAL_ENTRY_CANCELLED", cancelledPrompt.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, prompt.Calls);
        Assert.Equal(0, client.LoginCalls);
    }

    [Fact]
    public async Task NativeCredentialEntryPermitsExactlyOneLoginAndAttemptTokenIsConsumed()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out _);
        var prompt = new FakeHumanCredentialPrompt();
        var handler = new HumanSessionBridgeHandler(runtime, prompt);

        var result = await SendBridgeCommandAsync(
            handler,
            HumanSessionBridgeCommand.Login,
            new LoginPayload("cashier.synthetic"));

        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, prompt.Calls);
        Assert.Equal(1, client.LoginCalls);

        var gate = new HumanCredentialAttemptGate();
        Assert.True(gate.TryBegin(HumanCredentialOperation.Login, 7, out var attempt));
        using var credential = gate.TryConsume(
            AcceptedPromptResult(attempt),
            HumanCredentialOperation.Login,
            7,
            "host-correlation");
        Assert.NotNull(credential);
        Assert.Null(gate.TryConsume(
            AcceptedPromptResult(attempt),
            HumanCredentialOperation.Login,
            7,
            "host-correlation"));
    }

    [Fact]
    public void PreviousOrExpiredNativeAttemptCannotAuthorizeCredentialUse()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-09T00:00:00Z"));
        var gate = new HumanCredentialAttemptGate(time, TimeSpan.FromSeconds(30));
        Assert.True(gate.TryBegin(HumanCredentialOperation.Login, 1, out var previous));
        Assert.False(gate.TryBegin(HumanCredentialOperation.Login, 1, out _));
        gate.Invalidate(previous.AttemptReference);
        Assert.True(gate.TryBegin(HumanCredentialOperation.Login, 2, out var current));
        Assert.Null(gate.TryConsume(AcceptedPromptResult(previous), HumanCredentialOperation.Login, 1, "old"));
        using var currentCredential = gate.TryConsume(AcceptedPromptResult(current), HumanCredentialOperation.Login, 2, "current");
        Assert.NotNull(currentCredential);

        Assert.True(gate.TryBegin(HumanCredentialOperation.Reauthenticate, 2, out var expired));
        time.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(gate.TryConsume(AcceptedPromptResult(expired), HumanCredentialOperation.Reauthenticate, 2, "expired"));
    }

    [Fact]
    public async Task WrongOrStaleNativeAttemptCannotReachCentralPms()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out _);
        var prompt = new FakeHumanCredentialPrompt { ReturnedAttemptReference = Guid.NewGuid() };
        var handler = new HumanSessionBridgeHandler(runtime, prompt);

        var result = await SendBridgeCommandAsync(
            handler,
            HumanSessionBridgeCommand.Login,
            new LoginPayload("cashier.synthetic"));

        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("EXPLICIT_CREDENTIAL_ENTRY_REQUIRED", result.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(0, client.LoginCalls);
    }

    [Fact]
    public async Task ReauthenticationRejectsBrowserPasswordAndRequiresANewNativeAttempt()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out _);
        var prompt = new FakeHumanCredentialPrompt();
        var handler = new HumanSessionBridgeHandler(runtime, prompt);
        await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Login, new LoginPayload("cashier.synthetic"));

        var legacyPayload = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Reauthenticate, new Dictionary<string, string>
        {
            ["password"] = "browser-restored-value"
        });
        var explicitPrompt = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Reauthenticate, new { });

        Assert.False(legacyPayload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("MALFORMED_REQUEST", legacyPayload.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.True(explicitPrompt.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, prompt.Calls);
        Assert.Equal(1, client.LoginCalls);
        Assert.Equal(1, client.ReauthenticateCalls);
    }

    [Theory]
    [InlineData("SESSION_REVOKED", false)]
    [InlineData("SESSION_EXPIRED", false)]
    [InlineData("CENTRAL_PMS_UNAVAILABLE", true)]
    public async Task AuthorityLossNeverPresentsCredentialPromptOrCreatesPasswordSession(string errorCode, bool retryable)
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out var journal);
        var prompt = new FakeHumanCredentialPrompt();
        var handler = new HumanSessionBridgeHandler(runtime, prompt);
        await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Login, new LoginPayload("cashier.synthetic"));
        await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.OpenOrResumeShift, new { });
        await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.OpenOrResumeCustody, new OpenCustodyPayload(100m));
        client.GetResult = Failure(errorCode, retryable);

        var locked = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Refresh, new { });
        var secondRefresh = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Refresh, new { });
        var durable = await journal.GetLocalOperationalStateAsync(new LocalOperationalStateRequest(
            CashierId: CashierId.ToString("D"),
            TerminalId: "APT-TERMINAL-001",
            SiteId: SiteId.ToString("D"),
            SiteGroupId: SiteGroupId.ToString("D"),
            PosServerId: "POS-001"));

        Assert.False(locked.RootElement.GetProperty("payload").GetProperty("authenticated").GetBoolean());
        Assert.False(secondRefresh.RootElement.GetProperty("payload").GetProperty("authenticated").GetBoolean());
        Assert.Equal(1, prompt.Calls);
        Assert.Equal(1, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
        Assert.NotNull(durable.ActiveShift);
        Assert.NotNull(durable.ActiveCashCustodySession);
    }

    [Fact]
    public async Task ConcurrentLoginCommandsPermitOnePromptAndOneCentralPmsPasswordRequest()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out _);
        var prompt = new BlockingHumanCredentialPrompt { BlockFromCall = 1 };
        var handler = new HumanSessionBridgeHandler(runtime, prompt);

        var first = SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Login, new LoginPayload("cashier.synthetic"));
        await prompt.WaitForCallAsync(1);
        var duplicate = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Login, new LoginPayload("cashier.synthetic"));

        Assert.False(duplicate.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("CREDENTIAL_ENTRY_IN_PROGRESS", duplicate.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, prompt.Calls);
        Assert.Equal(0, client.LoginCalls);

        prompt.SubmitActive();
        var authenticated = await first;
        Assert.True(authenticated.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, client.LoginCalls);
    }

    [Fact]
    public async Task ThreeAuthorityLossCyclesCancelPendingPromptAndNeverReplayPasswordAuthentication()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out var journal);
        var prompt = new BlockingHumanCredentialPrompt { BlockFromCall = 2 };
        var handler = new HumanSessionBridgeHandler(runtime, prompt);
        await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Login, new LoginPayload("cashier.synthetic"));
        await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.OpenOrResumeShift, new { });
        await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.OpenOrResumeCustody, new OpenCustodyPayload(100m));

        var pendingReauthentication = SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Reauthenticate, new { });
        await prompt.WaitForCallAsync(2);
        client.GetResult = Failure("SESSION_REVOKED");

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var locked = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Refresh, new { });
            Assert.False(locked.RootElement.GetProperty("payload").GetProperty("authenticated").GetBoolean());
        }

        var cancelled = await pendingReauthentication;
        prompt.SubmitActive();
        var durable = await journal.GetLocalOperationalStateAsync(new LocalOperationalStateRequest(
            CashierId: CashierId.ToString("D"),
            TerminalId: "APT-TERMINAL-001",
            SiteId: SiteId.ToString("D"),
            SiteGroupId: SiteGroupId.ToString("D"),
            PosServerId: "POS-001"));

        Assert.False(cancelled.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("CREDENTIAL_ENTRY_CANCELLED", cancelled.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(1, prompt.CancelCalls);
        Assert.Equal(1, client.LoginCalls);
        Assert.Equal(0, client.ReauthenticateCalls);
        Assert.NotNull(durable.ActiveShift);
        Assert.NotNull(durable.ActiveCashCustodySession);

        prompt.BlockFromCall = int.MaxValue;
        var explicitRecovery = await SendBridgeCommandAsync(handler, HumanSessionBridgeCommand.Login, new LoginPayload("cashier.synthetic"));
        Assert.True(explicitRecovery.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, client.LoginCalls);
    }

    [Fact]
    public async Task ExplicitCredentialSubmissionCanReachRuntimeOnlyOnce()
    {
        var client = new FakeHumanSessionClient(Success(CashierId));
        var runtime = CreateRuntime(client, new MemoryCredentialStore(), out _);
        var credential = Credential(runtime);

        var authenticated = await runtime.LoginAsync("cashier.synthetic", credential);
        var replay = await runtime.LoginAsync("cashier.synthetic", credential);

        Assert.True(authenticated.Authenticated);
        Assert.False(replay.Authenticated);
        Assert.Equal("INVALID_LOGIN_REQUEST", replay.ErrorCode);
        Assert.Equal(1, client.LoginCalls);
    }

    [Fact]
    public void NormalDesktopRuntimeAllowsOnlyOneProcessPerTerminal()
    {
        var terminal = $"APT-TEST-{Guid.NewGuid():N}";
        using var first = DesktopSingleInstanceLease.TryAcquire(terminal);
        using var second = DesktopSingleInstanceLease.TryAcquire(terminal);
        using var otherTerminal = DesktopSingleInstanceLease.TryAcquire(terminal + "-OTHER");

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.NotNull(otherTerminal);
    }

    [Fact]
    public async Task CentralPmsClientUsesI020RoutesAndDeviceBoundSessionAuthorization()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var handler = new DelegateHttpHandler(async request =>
        {
            captured = request;
            body = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return JsonResponse(Success(CashierId).Response!);
        });
        var client = new CentralPmsHumanSessionClient(new HttpClient(handler), "https://central-pms.invalid", DeviceId.ToString("D"));

        await client.LoginAsync("cashier.synthetic", "synthetic-password", SiteId, Guid.NewGuid(), default);
        Assert.Equal("/v1/apt/human-sessions", captured!.RequestUri!.AbsolutePath);
        Assert.Equal(DeviceId.ToString("D"), captured.Headers.GetValues("X-ExitPass-Service-Identity-Id").Single());
        Assert.Contains("\"username\":\"cashier.synthetic\"", body, StringComparison.Ordinal);
        Assert.Contains("\"totpCode\":null", body, StringComparison.Ordinal);

        await client.GetAsync(Guid.Parse("66666666-6666-4666-8666-666666666666"), "opaque-session-token", Guid.NewGuid(), default);
        Assert.Equal("/v1/apt/human-sessions/66666666-6666-4666-8666-666666666666", captured.RequestUri!.AbsolutePath);
        Assert.Equal("ExitPass-HumanSession", captured.Headers.Authorization!.Scheme);
        Assert.Equal("opaque-session-token", captured.Headers.Authorization.Parameter);
        Assert.True(captured.Headers.CacheControl!.NoCache);
        Assert.True(captured.Headers.CacheControl.NoStore);

        await client.ContinueAsync(Guid.Parse("66666666-6666-4666-8666-666666666666"), "opaque-session-token", Guid.NewGuid(), default);
        Assert.Equal("/v1/apt/human-sessions/66666666-6666-4666-8666-666666666666/continue", captured.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, captured.Method);

        await client.ReauthenticateAsync(Guid.Parse("66666666-6666-4666-8666-666666666666"), "opaque-session-token", "fresh-password", Guid.NewGuid(), default);
        Assert.Equal("/v1/apt/human-sessions/66666666-6666-4666-8666-666666666666/reauthenticate", captured.RequestUri!.AbsolutePath);
        Assert.Contains("\"password\":\"fresh-password\"", body, StringComparison.Ordinal);

        await client.LogoutAsync(Guid.Parse("66666666-6666-4666-8666-666666666666"), "opaque-session-token", Guid.NewGuid(), default);
        Assert.Equal("/v1/apt/human-sessions/66666666-6666-4666-8666-666666666666/logout", captured.RequestUri!.AbsolutePath);
        Assert.Equal("ExitPass-HumanSession", captured.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task CentralPmsClientClassifiesCanonicalRevokedReadbackAsTerminalSessionFailure()
    {
        var correlationId = Guid.NewGuid();
        var response = new AptHumanAuthenticationResponse(
            "SESSION_EXPIRED",
            false,
            null,
            null,
            "SESSION_EXPIRED",
            false,
            correlationId);
        var handler = new DelegateHttpHandler(_ => Task.FromResult(JsonResponse(response, HttpStatusCode.Unauthorized)));
        var client = new CentralPmsHumanSessionClient(new HttpClient(handler), "https://central-pms.invalid", DeviceId.ToString("D"));

        var result = await client.GetAsync(Guid.NewGuid(), "revoked-session-token", Guid.NewGuid(), default);

        Assert.False(result.Ok);
        Assert.Equal("SESSION_EXPIRED", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Fact]
    public async Task CanonicalRevokedHttpReadbackClearsRuntimeAuthorityAndPreservesDurableAccountability()
    {
        var requestCount = 0;
        var revokedCorrelationId = Guid.NewGuid();
        var revoked = new AptHumanAuthenticationResponse(
            "SESSION_EXPIRED",
            false,
            null,
            null,
            "SESSION_EXPIRED",
            false,
            revokedCorrelationId);
        var handler = new DelegateHttpHandler(_ => Task.FromResult(
            ++requestCount <= 3
                ? JsonResponse(Success(CashierId).Response!)
                : JsonResponse(revoked, HttpStatusCode.Unauthorized)));
        var client = new CentralPmsHumanSessionClient(new HttpClient(handler), "https://central-pms.invalid", DeviceId.ToString("D"));
        var store = new MemoryCredentialStore();
        var runtime = new HumanSessionRuntime(
            client,
            store,
            CreateJournal(),
            new HumanSessionRuntimeOptions("APT-TERMINAL-001", SiteId, SiteGroupId, "POS-001", DeviceId));
        await runtime.LoginAsync("cashier.synthetic", Credential(runtime));
        await runtime.OpenOrResumeShiftAsync();
        await runtime.OpenOrResumeCustodyAsync(100m);

        var locked = await runtime.RefreshAsync();

        Assert.False(locked.Authenticated);
        Assert.Equal("LOCKED", locked.AuthenticationState);
        Assert.Equal("SESSION_EXPIRED", locked.ErrorCode);
        Assert.False(locked.ShiftOperationsAuthorized);
        Assert.False(locked.CustodyOperationsAuthorized);
        Assert.False(locked.CashOperationsAuthorized);
        Assert.NotNull(locked.ActiveShift);
        Assert.NotNull(locked.ActiveCashCustodySession);
        Assert.Null(store.Credential);
        Assert.Equal(4, requestCount);
    }

    [Fact]
    public async Task MalformedUnauthorizedReadbackCannotRemainAuthenticatedOrRetainAValidSessionClassification()
    {
        var handler = new DelegateHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "text/plain")
        }));
        var client = new CentralPmsHumanSessionClient(new HttpClient(handler), "https://central-pms.invalid", DeviceId.ToString("D"));

        var result = await client.GetAsync(Guid.NewGuid(), "invalid-session-token", Guid.NewGuid(), default);

        Assert.False(result.Ok);
        Assert.Equal("SESSION_INVALID", result.ErrorCode);
        Assert.False(result.Retryable);
    }

    [Fact]
    public void FrontendMockModeEnvironmentFlagCannotReplaceTheHostI020Configuration()
    {
        var names = new[]
        {
            "USE_MOCK_CENTRAL_PMS", "CENTRAL_PMS_BASE_URL", "APT_CENTRAL_PMS_SERVICE_IDENTITY_ID",
            "APT_TERMINAL_ID", "APT_SITE_ID", "APT_SITE_GROUP_ID", "APT_POS_SERVER_ID"
        };
        var prior = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("USE_MOCK_CENTRAL_PMS", "true");
            Environment.SetEnvironmentVariable("CENTRAL_PMS_BASE_URL", "https://central-pms.i020.invalid");
            Environment.SetEnvironmentVariable("APT_CENTRAL_PMS_SERVICE_IDENTITY_ID", DeviceId.ToString("D"));
            Environment.SetEnvironmentVariable("APT_TERMINAL_ID", "APT-TERMINAL-001");
            Environment.SetEnvironmentVariable("APT_SITE_ID", SiteId.ToString("D"));
            Environment.SetEnvironmentVariable("APT_SITE_GROUP_ID", SiteGroupId.ToString("D"));
            Environment.SetEnvironmentVariable("APT_POS_SERVER_ID", "POS-001");

            var options = StartupOptions.FromEnvironmentAndArgs([]);
            var humanOptions = MainWindow.CreateHumanSessionOptions(options);

            Assert.Equal("https://central-pms.i020.invalid", options.CentralPmsBaseUrl);
            Assert.Equal(DeviceId.ToString("D"), options.CentralPmsServiceIdentityId);
            Assert.NotNull(humanOptions);
            Assert.Equal(DeviceId, humanOptions!.DeviceServiceIdentityId);
            Assert.DoesNotContain(
                typeof(StartupOptions).GetProperties(),
                property => property.Name.Contains("MockCentralPms", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, prior[name]);
            }
        }
    }

    private HumanSessionRuntime CreateRuntime(FakeHumanSessionClient client, MemoryCredentialStore store, out CashJournalService journal)
    {
        journal = CreateJournal();
        return CreateRuntime(client, store, journal);
    }

    private static HumanSessionRuntime CreateRuntime(FakeHumanSessionClient client, MemoryCredentialStore store, CashJournalService journal) =>
        new(client, store, journal, new HumanSessionRuntimeOptions("APT-TERMINAL-001", SiteId, SiteGroupId, "POS-001", DeviceId));

    private static ExplicitHumanCredentialSubmission Credential(
        HumanSessionRuntime runtime,
        HumanCredentialOperation operation = HumanCredentialOperation.Login,
        string credentialValue = "not-a-real-password") =>
        new(Guid.NewGuid(), operation, runtime.AuthorityVersion, Guid.NewGuid().ToString("N"), credentialValue);

    private static HumanCredentialPromptResult AcceptedPromptResult(HumanCredentialAttempt attempt) =>
        new(attempt.AttemptReference, true, "not-a-real-password", "NATIVE_EXPLICIT_SUBMIT");

    private CashJournalService CreateJournal()
    {
        var databaseDirectory = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(databaseDirectory);
        return new(new LocalOperationsDatabaseOptions(Path.Combine(databaseDirectory, "cash-journal.db")));
    }

    private static OpenCashierShiftRequest OpenShift(Guid cashierId, string shiftId) =>
        new(shiftId, cashierId.ToString("D"), Guid.NewGuid().ToString("D"), "APT-TERMINAL-001", SiteId.ToString("D"), SiteGroupId.ToString("D"), "POS-001");

    private static CreateCashCustodySessionRequest CreateCustody(Guid cashierId, string shiftId) =>
        new(cashierId.ToString("D"), Guid.NewGuid().ToString("D"), shiftId, "APT-TERMINAL-001", SiteId.ToString("D"), SiteGroupId.ToString("D"), "POS-001", 100m);

    private static HumanSessionClientResult Success(
        Guid cashierId,
        string audience = "APT",
        Guid? deviceId = null,
        IReadOnlyList<Guid>? siteIds = null,
        IReadOnlyList<Guid>? siteGroupIds = null,
        IReadOnlyList<string>? permissions = null,
        Guid? sessionReference = null,
        string token = "opaque-session-token",
        bool hasGlobalScope = false)
    {
        var correlationId = Guid.NewGuid();
        var session = new AptHumanSessionDto(
            sessionReference ?? Guid.NewGuid(), cashierId, "cashier.synthetic", "Synthetic Cashier", audience, "PASSWORD",
            false, false, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddHours(12),
            permissions ?? AllOperationalPermissions, siteIds ?? [SiteId], siteGroupIds ?? [SiteGroupId],
            hasGlobalScope, deviceId ?? DeviceId, correlationId);
        return HumanSessionClientResult.Success(new AptHumanAuthenticationResponse("AUTHENTICATED", true, session, token, null, false, correlationId));
    }

    private static HumanSessionClientResult Failure(string code, bool retryable = false) =>
        HumanSessionClientResult.Failure(code, "Online cashier authority is unavailable.", Guid.NewGuid(), retryable);

    private static string[] Without(string permission) =>
        AllOperationalPermissions.Where(value => !string.Equals(value, permission, StringComparison.Ordinal)).ToArray();

    private static HttpResponseMessage JsonResponse(AptHumanAuthenticationResponse response, HttpStatusCode statusCode = HttpStatusCode.OK) =>
        new(statusCode) { Content = new StringContent(JsonSerializer.Serialize(response, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };

    private static async Task<JsonDocument> SendBridgeCommandAsync(HumanSessionBridgeHandler handler, string command, object payload)
    {
        var response = await handler.HandleWebMessageAsync(JsonSerializer.Serialize(new
        {
            source = HumanSessionBridgeCommand.Source,
            command,
            correlationId = Guid.NewGuid().ToString("N"),
            payload
        }));
        Assert.NotNull(response);
        return JsonDocument.Parse(response!);
    }

    private static void AssertOwnedOpenShift(JsonDocument response)
    {
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var shift = response.RootElement.GetProperty("payload").GetProperty("activeShift");
        Assert.Equal(CashierId.ToString("D"), shift.GetProperty("cashierId").GetString());
        Assert.Equal(JsonValueKind.String, shift.GetProperty("status").ValueKind);
        Assert.Equal("Open", shift.GetProperty("status").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class MemoryCredentialStore : IHumanSessionCredentialStore
    {
        public HumanSessionCredential? Credential { get; set; }
        public HumanSessionCredential? Load() => Credential;
        public void Save(HumanSessionCredential credential) => Credential = credential;
        public void Delete() => Credential = null;
    }

    private sealed class FakeHumanSessionClient(HumanSessionClientResult initial) : ICentralPmsHumanSessionClient
    {
        public HumanSessionClientResult LoginResult { get; set; } = initial;
        public HumanSessionClientResult GetResult { get; set; } = initial;
        public HumanSessionClientResult ContinueResult { get; set; } = initial;
        public int ContinueCalls { get; private set; }
        public int LoginCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int ReauthenticateCalls { get; private set; }
        public Task<HumanSessionClientResult> LoginAsync(string username, string password, Guid siteId, Guid correlationId, CancellationToken cancellationToken) { LoginCalls++; return Task.FromResult(LoginResult); }
        public Task<HumanSessionClientResult> GetAsync(Guid sessionReference, string sessionToken, Guid correlationId, CancellationToken cancellationToken) { GetCalls++; return Task.FromResult(GetResult); }
        public Task<HumanSessionClientResult> ContinueAsync(Guid sessionReference, string sessionToken, Guid correlationId, CancellationToken cancellationToken) { ContinueCalls++; return Task.FromResult(ContinueResult); }
        public Task<HumanSessionClientResult> ReauthenticateAsync(Guid sessionReference, string sessionToken, string password, Guid correlationId, CancellationToken cancellationToken) { ReauthenticateCalls++; return Task.FromResult(LoginResult); }
        public Task<HumanSessionClientResult> LogoutAsync(Guid sessionReference, string sessionToken, Guid correlationId, CancellationToken cancellationToken) => Task.FromResult(LoginResult);
    }

    private sealed class FakeHumanCredentialPrompt : IHumanCredentialPrompt
    {
        public bool Accepted { get; set; } = true;
        public Guid? ReturnedAttemptReference { get; set; }
        public int Calls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<HumanCredentialPromptResult> PromptAsync(
            HumanCredentialPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new HumanCredentialPromptResult(
                ReturnedAttemptReference ?? request.AttemptReference,
                Accepted,
                Accepted ? "not-a-real-password" : null,
                Accepted ? "NATIVE_EXPLICIT_SUBMIT" : "CANCELLED"));
        }

        public void CancelActive(string reason) => CancelCalls++;
    }

    private sealed class BlockingHumanCredentialPrompt : IHumanCredentialPrompt
    {
        private readonly object _sync = new();
        private readonly Dictionary<int, TaskCompletionSource<bool>> _callWaiters = [];
        private TaskCompletionSource<HumanCredentialPromptResult>? _pending;
        private HumanCredentialPromptRequest? _pendingRequest;

        public int BlockFromCall { get; set; } = int.MaxValue;
        public int Calls { get; private set; }
        public int CancelCalls { get; private set; }

        public Task<HumanCredentialPromptResult> PromptAsync(
            HumanCredentialPromptRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                Calls++;
                if (_callWaiters.Remove(Calls, out var waiter))
                {
                    waiter.TrySetResult(true);
                }
                if (Calls < BlockFromCall)
                {
                    return Task.FromResult(new HumanCredentialPromptResult(
                        request.AttemptReference,
                        true,
                        "not-a-real-password",
                        "NATIVE_EXPLICIT_SUBMIT"));
                }

                _pendingRequest = request;
                _pending = new TaskCompletionSource<HumanCredentialPromptResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                return _pending.Task;
            }
        }

        public Task WaitForCallAsync(int call)
        {
            lock (_sync)
            {
                if (Calls >= call)
                {
                    return Task.CompletedTask;
                }
                if (!_callWaiters.TryGetValue(call, out var waiter))
                {
                    waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _callWaiters[call] = waiter;
                }
                return waiter.Task;
            }
        }

        public void SubmitActive()
        {
            lock (_sync)
            {
                if (_pending is null || _pendingRequest is null)
                {
                    return;
                }
                _pending.TrySetResult(new HumanCredentialPromptResult(
                    _pendingRequest.AttemptReference,
                    true,
                    "not-a-real-password",
                    "NATIVE_EXPLICIT_SUBMIT"));
                _pending = null;
                _pendingRequest = null;
            }
        }

        public void CancelActive(string reason)
        {
            lock (_sync)
            {
                if (_pending is null || _pendingRequest is null)
                {
                    return;
                }
                CancelCalls++;
                _pending.TrySetResult(new HumanCredentialPromptResult(
                    _pendingRequest.AttemptReference,
                    false,
                    null,
                    "CANCELLED"));
                _pending = null;
                _pendingRequest = null;
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    private sealed class DelegateHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
