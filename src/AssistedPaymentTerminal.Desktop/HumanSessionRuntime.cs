using AssistedPaymentTerminal.LocalOperations;
using System.IO;

namespace AssistedPaymentTerminal.Desktop;

public interface IHumanCashAuthorization
{
    Task<HumanCashAuthorizationResult> AuthorizeCashAsync(CancellationToken cancellationToken = default);
}

public sealed record HumanCashAuthorizationResult(bool Authorized, string Code, string SafeMessage);

public sealed record HumanSessionRuntimeOptions(
    string TerminalId,
    Guid SiteId,
    Guid SiteGroupId,
    string PosServerId,
    Guid DeviceServiceIdentityId)
{
    public const string AccessPermission = "apt.access";
    public const string ShiftOperatePermission = "cashier-shifts.operate";
    public const string CustodyOperatePermission = "cash-custody.operate";
    public const string CashReceivePermission = "terminal-cash.receive";
}

public sealed class HumanSessionRuntime : IHumanCashAuthorization
{
    private readonly ICentralPmsHumanSessionClient _client;
    private readonly IHumanSessionCredentialStore _credentialStore;
    private readonly CashJournalService _journal;
    private readonly HumanSessionRuntimeOptions? _options;
    private readonly HumanAuthenticationTrace _trace;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private HumanSessionCredential? _credential;
    private AptHumanSessionDto? _session;
    private HumanSessionSafeState _lastState;
    private long _authorityVersion;

    public HumanSessionRuntime(
        ICentralPmsHumanSessionClient client,
        IHumanSessionCredentialStore credentialStore,
        CashJournalService journal,
        HumanSessionRuntimeOptions? options,
        HumanAuthenticationTrace? trace = null)
    {
        _client = client;
        _credentialStore = credentialStore;
        _journal = journal;
        _options = options;
        _trace = trace ?? new HumanAuthenticationTrace();
        _lastState = HumanSessionSafeState.Unauthenticated(
            options is not null,
            options is null ? "This terminal is not configured for Central PMS device-bound human login." : "Cashier sign-in is required.");
    }

    public HumanSessionSafeState CurrentState => _lastState;
    public long AuthorityVersion => Interlocked.Read(ref _authorityVersion);

    public async Task<HumanSessionSafeState> RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_options is null)
            {
                return _lastState;
            }

            _credential = _credentialStore.Load();
            if (_credential is null)
            {
                return _lastState = HumanSessionSafeState.Unauthenticated(true, "Cashier sign-in is required.");
            }

            var result = await _client.ContinueAsync(
                _credential.SessionReference,
                _credential.SessionToken,
                Guid.NewGuid(),
                cancellationToken).ConfigureAwait(false);
            return await ApplyAuthoritativeResultAsync(result, "Session restored after online validation.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> LoginAsync(
        string username,
        ExplicitHumanCredentialSubmission credentialSubmission,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_options is null)
            {
                return _lastState = HumanSessionSafeState.Unavailable("APT_DEVICE_TRUST_UNAVAILABLE", "This terminal has not established the approved device trust boundary.", false);
            }
            if (string.IsNullOrWhiteSpace(username)
                || !credentialSubmission.TryConsume(HumanCredentialOperation.Login, AuthorityVersion, out var password))
            {
                return _lastState = HumanSessionSafeState.Unauthenticated(true, "Enter a username and password.") with { ErrorCode = "INVALID_LOGIN_REQUEST" };
            }

            var correlationId = Guid.NewGuid();
            _trace.Record("runtime.password-authentication-starting", "LOGIN", nameof(LoginAsync), "EXPLICIT_NATIVE_CREDENTIAL", true, credentialSubmission.AttemptReference, credentialSubmission.HostCorrelationId, correlationId);
            var result = await _client.LoginAsync(username.Trim(), password, _options.SiteId, correlationId, cancellationToken).ConfigureAwait(false);
            _trace.Record("runtime.password-authentication-completed", "LOGIN", nameof(LoginAsync), "EXPLICIT_NATIVE_CREDENTIAL", true, credentialSubmission.AttemptReference, credentialSubmission.HostCorrelationId, correlationId, result.Ok ? "SUCCESS" : result.ErrorCode);
            return await ApplyAuthoritativeResultAsync(result, "Cashier authenticated by Central PMS.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            credentialSubmission.Dispose();
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> ReauthenticateAsync(
        ExplicitHumanCredentialSubmission credentialSubmission,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_credential is null
                || _session is null
                || !credentialSubmission.TryConsume(HumanCredentialOperation.Reauthenticate, AuthorityVersion, out var password))
            {
                return _lastState with
                {
                    AuthenticationState = "LOCKED",
                    Authenticated = false,
                    CashOperationsAuthorized = false,
                    ErrorCode = "REAUTHENTICATION_REQUIRED",
                    SafeMessage = "Sign in again with the same cashier account."
                };
            }

            var correlationId = Guid.NewGuid();
            _trace.Record("runtime.password-authentication-starting", "REAUTHENTICATE", nameof(ReauthenticateAsync), "EXPLICIT_NATIVE_CREDENTIAL", true, credentialSubmission.AttemptReference, credentialSubmission.HostCorrelationId, correlationId);
            var result = await _client.ReauthenticateAsync(
                _credential.SessionReference,
                _credential.SessionToken,
                password,
                correlationId,
                cancellationToken).ConfigureAwait(false);
            _trace.Record("runtime.password-authentication-completed", "REAUTHENTICATE", nameof(ReauthenticateAsync), "EXPLICIT_NATIVE_CREDENTIAL", true, credentialSubmission.AttemptReference, credentialSubmission.HostCorrelationId, correlationId, result.Ok ? "SUCCESS" : result.ErrorCode);
            return await ApplyAuthoritativeResultAsync(result, "Cashier authority was freshly reauthenticated.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            credentialSubmission.Dispose();
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> OpenOrResumeShiftAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.Authenticated || _session is null || _options is null)
            {
                return refreshed;
            }
            if (!HasPermission(_session, HumanSessionRuntimeOptions.ShiftOperatePermission))
            {
                return _lastState = refreshed with
                {
                    ShiftOperationsAuthorized = false,
                    CashOperationsAuthorized = false,
                    ErrorCode = "SHIFT_PERMISSION_DENIED",
                    SafeMessage = "This cashier is not currently authorized to operate a cashier shift."
                };
            }
            if (!refreshed.ShiftOperationsAuthorized)
            {
                return refreshed;
            }

            var ownState = await LoadOwnOperationalStateAsync(_session.UserReference, cancellationToken).ConfigureAwait(false);
            if (ownState.ActiveShift is not null)
            {
                return _lastState = await BuildSafeStateAsync(_session, "Own open shift resumed.", cancellationToken).ConfigureAwait(false);
            }

            var terminalState = await LoadTerminalOperationalStateAsync(cancellationToken).ConfigureAwait(false);
            if (terminalState.ActiveShift is not null || terminalState.ActiveCashCustodySession is not null)
            {
                return _lastState = await BuildSafeStateAsync(_session, "Another cashier has open terminal accountability. Shift inheritance is prohibited.", cancellationToken, "CROSS_CASHIER_SHIFT_BLOCKED").ConfigureAwait(false);
            }

            var opened = await _journal.OpenCashierShiftAsync(new OpenCashierShiftRequest(
                CashierShiftId: $"SHIFT-{Guid.NewGuid():N}",
                CashierId: _session.UserReference.ToString("D"),
                AuthenticatedCashierSessionReference: _session.SessionReference.ToString("D"),
                TerminalId: _options.TerminalId,
                SiteId: _options.SiteId.ToString("D"),
                SiteGroupId: _options.SiteGroupId.ToString("D"),
                PosServerId: _options.PosServerId), cancellationToken).ConfigureAwait(false);
            if (!opened.IsSuccess)
            {
                return _lastState = await BuildSafeStateAsync(_session, "The cashier shift could not be opened safely.", cancellationToken, "SHIFT_OPEN_FAILED").ConfigureAwait(false);
            }

            return _lastState = await BuildSafeStateAsync(_session, "Cashier shift opened.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> OpenOrResumeCustodyAsync(decimal openingCashAmount, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.Authenticated || _session is null || _options is null)
            {
                return refreshed;
            }
            if (!HasPermission(_session, HumanSessionRuntimeOptions.CustodyOperatePermission))
            {
                return _lastState = refreshed with
                {
                    CustodyOperationsAuthorized = false,
                    CashOperationsAuthorized = false,
                    ErrorCode = "CUSTODY_PERMISSION_DENIED",
                    SafeMessage = "This cashier is not currently authorized to operate cash custody."
                };
            }
            if (!refreshed.CustodyOperationsAuthorized)
            {
                return refreshed;
            }
            if (openingCashAmount < 0)
            {
                return _lastState = refreshed with { ErrorCode = "INVALID_OPENING_CASH_AMOUNT", SafeMessage = "Opening cash amount cannot be negative." };
            }

            var ownState = await LoadOwnOperationalStateAsync(_session.UserReference, cancellationToken).ConfigureAwait(false);
            if (ownState.ActiveShift is null)
            {
                return _lastState = refreshed with { ErrorCode = "SHIFT_REQUIRED", SafeMessage = "Open or resume your cashier shift before opening custody." };
            }
            if (ownState.ActiveCashCustodySession is not null)
            {
                return _lastState = await BuildSafeStateAsync(_session, "Own open cash custody resumed.", cancellationToken).ConfigureAwait(false);
            }

            var terminalState = await LoadTerminalOperationalStateAsync(cancellationToken).ConfigureAwait(false);
            if (terminalState.ActiveCashCustodySession is not null)
            {
                return _lastState = await BuildSafeStateAsync(_session, "Another cashier has open cash custody. Custody inheritance is prohibited.", cancellationToken, "CROSS_CASHIER_CUSTODY_BLOCKED").ConfigureAwait(false);
            }

            var opened = await _journal.CreateCashCustodySessionAsync(new CreateCashCustodySessionRequest(
                CashierId: _session.UserReference.ToString("D"),
                AuthenticatedCashierSessionReference: _session.SessionReference.ToString("D"),
                CashierShiftId: ownState.ActiveShift.Id,
                TerminalId: _options.TerminalId,
                SiteId: _options.SiteId.ToString("D"),
                SiteGroupId: _options.SiteGroupId.ToString("D"),
                PosServerId: _options.PosServerId,
                OpeningCashAmount: openingCashAmount), cancellationToken).ConfigureAwait(false);
            if (!opened.IsSuccess)
            {
                return _lastState = await BuildSafeStateAsync(_session, "Cash custody could not be opened safely.", cancellationToken, "CUSTODY_OPEN_FAILED").ConfigureAwait(false);
            }

            return _lastState = await BuildSafeStateAsync(_session, "Cash custody opened.", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                var state = await LoadOwnOperationalStateAsync(_session.UserReference, cancellationToken).ConfigureAwait(false);
                if (state.ActiveCashCustodySession is not null)
                {
                    return _lastState = await BuildSafeStateAsync(
                        _session,
                        "Sign out is unavailable while you have open cash custody. Custody remains open for governed recovery.",
                        cancellationToken,
                        "OPEN_CUSTODY_LOGOUT_BLOCKED").ConfigureAwait(false);
                }
            }

            if (_credential is not null)
            {
                var result = await _client.LogoutAsync(
                    _credential.SessionReference,
                    _credential.SessionToken,
                    Guid.NewGuid(),
                    cancellationToken).ConfigureAwait(false);
                if (!result.Ok && result.ErrorCode is not "SESSION_INVALID" and not "SESSION_EXPIRED" and not "SESSION_REVOKED" and not "SESSION_NOT_FOUND")
                {
                    return _lastState = HumanSessionSafeState.Unavailable(result.ErrorCode ?? "LOGOUT_FAILED", result.SafeMessage ?? "Logout could not be confirmed by Central PMS.", result.Retryable);
                }
            }

            ClearCredential();
            return _lastState = HumanSessionSafeState.Unauthenticated(_options is not null, "Cashier signed out. Shift and custody records were not changed.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<HumanCashAuthorizationResult> AuthorizeCashAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!state.Authenticated || _session is null)
            {
                return new HumanCashAuthorizationResult(false, state.ErrorCode ?? "HUMAN_SESSION_REQUIRED", state.SafeMessage);
            }
            if (state.ErrorCode is "CROSS_CASHIER_SHIFT_BLOCKED" or "CROSS_CASHIER_CUSTODY_BLOCKED")
            {
                return new HumanCashAuthorizationResult(false, state.ErrorCode, state.SafeMessage);
            }
            if (!HasPermission(_session, HumanSessionRuntimeOptions.ShiftOperatePermission))
            {
                return new HumanCashAuthorizationResult(false, "SHIFT_PERMISSION_DENIED", "Current cashier-shift authority is required before cash can be accepted.");
            }
            if (!HasPermission(_session, HumanSessionRuntimeOptions.CustodyOperatePermission))
            {
                return new HumanCashAuthorizationResult(false, "CUSTODY_PERMISSION_DENIED", "Current cash-custody authority is required before cash can be accepted.");
            }
            if (!HasPermission(_session, HumanSessionRuntimeOptions.CashReceivePermission))
            {
                return new HumanCashAuthorizationResult(false, "CASH_RECEIVE_PERMISSION_DENIED", "This cashier is not currently authorized to receive cash at this terminal.");
            }
            if (state.ActiveShift is null)
            {
                return new HumanCashAuthorizationResult(false, "SHIFT_REQUIRED", "An own active cashier shift is required before cash can be accepted.");
            }
            if (state.ActiveCashCustodySession is null)
            {
                return new HumanCashAuthorizationResult(false, "CUSTODY_REQUIRED", "An own active cash-custody session is required before cash can be accepted.");
            }
            return new HumanCashAuthorizationResult(true, "AUTHORIZED", "Current online cashier authority, shift, and custody are valid.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<HumanSessionSafeState> RefreshLockedAsync(CancellationToken cancellationToken)
    {
        if (_credential is null || _session is null)
        {
            var terminalLock = IsTerminalSessionFailure(_lastState.ErrorCode);
            return _lastState with
            {
                AuthenticationState = terminalLock ? "LOCKED" : "UNAUTHENTICATED",
                Authenticated = false,
                ShiftOperationsAuthorized = false,
                CustodyOperationsAuthorized = false,
                CashOperationsAuthorized = false,
                ErrorCode = terminalLock ? _lastState.ErrorCode : "HUMAN_SESSION_REQUIRED",
                SafeMessage = terminalLock
                    ? _lastState.SafeMessage
                    : "Cashier sign-in is required. Cached local identity cannot authorize cash."
            };
        }

        var retainedActionError = string.Equals(
            _lastState.ErrorCode,
            "OPEN_CUSTODY_LOGOUT_BLOCKED",
            StringComparison.Ordinal)
            ? (ErrorCode: _lastState.ErrorCode, SafeMessage: _lastState.SafeMessage)
            : (ErrorCode: (string?)null, SafeMessage: (string?)null);
        var result = await _client.GetAsync(
            _credential.SessionReference,
            _credential.SessionToken,
            Guid.NewGuid(),
            cancellationToken).ConfigureAwait(false);
        var refreshed = await ApplyAuthoritativeResultAsync(result, "Current cashier authority confirmed online.", cancellationToken).ConfigureAwait(false);
        if (refreshed.Authenticated && retainedActionError.ErrorCode is not null)
        {
            return _lastState = refreshed with
            {
                ErrorCode = retainedActionError.ErrorCode,
                SafeMessage = retainedActionError.SafeMessage ?? refreshed.SafeMessage
            };
        }
        return refreshed;
    }

    private async Task<HumanSessionSafeState> ApplyAuthoritativeResultAsync(
        HumanSessionClientResult result,
        string successMessage,
        CancellationToken cancellationToken)
    {
        if (!result.Ok || result.Response?.Session is null)
        {
            return await LockCurrentAuthorityAsync(
                result.ErrorCode ?? "HUMAN_SESSION_UNAVAILABLE",
                result.SafeMessage ?? "Online cashier authority is unavailable.",
                result.Retryable,
                cancellationToken).ConfigureAwait(false);
        }

        var response = result.Response;
        var session = response.Session;
        var validation = ValidateSession(session);
        if (!validation.Valid)
        {
            return await LockCurrentAuthorityAsync(
                validation.Code,
                validation.Message,
                false,
                cancellationToken).ConfigureAwait(false);
        }

        var token = string.IsNullOrWhiteSpace(response.AptSessionToken)
            ? _credential?.SessionToken
            : response.AptSessionToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            return await LockCurrentAuthorityAsync(
                "MISSING_APT_SESSION_TOKEN",
                "Central PMS did not provide usable APT session continuation material.",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        _session = session;
        _credential = new HumanSessionCredential(session.SessionReference, token);
        _credentialStore.Save(_credential);
        Interlocked.Increment(ref _authorityVersion);
        return _lastState = await BuildSafeStateAsync(session, successMessage, cancellationToken).ConfigureAwait(false);
    }

    private (bool Valid, string Code, string Message) ValidateSession(AptHumanSessionDto session)
    {
        if (_options is null)
        {
            return (false, "APT_DEVICE_TRUST_UNAVAILABLE", "This terminal has not established the approved device trust boundary.");
        }
        if (!string.Equals(session.Audience, "APT", StringComparison.Ordinal))
        {
            return (false, "WRONG_SESSION_AUDIENCE", "Central PMS returned a session for a different application audience.");
        }
        if (session.DeviceServiceIdentityReference != _options.DeviceServiceIdentityId)
        {
            return (false, "WRONG_DEVICE_SESSION", "The cashier session is not bound to this terminal device identity.");
        }
        if (session.PasswordChangeRequired || session.MfaRequired)
        {
            return (false, "SESSION_ASSURANCE_UNAVAILABLE", "This account requires an action that the APT cashier channel cannot perform.");
        }
        var now = DateTimeOffset.UtcNow;
        if (session.IdleExpiresAt <= now || session.AbsoluteExpiresAt <= now)
        {
            return (false, "SESSION_EXPIRED", "The cashier session expired. Sign in again to restore authority.");
        }
        var siteAllowed = session.SiteReferences.Contains(_options.SiteId)
            || session.SiteGroupReferences.Contains(_options.SiteGroupId);
        if (!siteAllowed)
        {
            return (false, "SITE_SCOPE_DENIED", "This cashier is not authorized for the terminal Site or Site Group.");
        }
        if (!HasPermission(session, HumanSessionRuntimeOptions.AccessPermission))
        {
            return (false, "APT_ACCESS_PERMISSION_DENIED", "This cashier is not currently authorized to use the Assisted Payment Terminal.");
        }
        return (true, "AUTHORIZED", "Current cashier authority is valid.");
    }

    private async Task<HumanSessionSafeState> LockCurrentAuthorityAsync(
        string code,
        string message,
        bool retryable,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _authorityVersion);
        var priorSession = _session;
        if (IsTerminalSessionFailure(code))
        {
            ClearCredential();
        }
        _session = null;

        var locked = HumanSessionSafeState.Unavailable(code, message, retryable) with
        {
            AuthenticationState = IsTerminalSessionFailure(code) ? "LOCKED" : "UNAVAILABLE"
        };
        if (priorSession is null || _options is null)
        {
            return _lastState = locked;
        }

        try
        {
            var ownState = await LoadOwnOperationalStateAsync(priorSession.UserReference, cancellationToken).ConfigureAwait(false);
            return _lastState = locked with
            {
                UserReference = priorSession.UserReference.ToString("D"),
                Username = priorSession.Username,
                DisplayName = priorSession.DisplayName,
                Audience = priorSession.Audience,
                Assurance = priorSession.Assurance,
                SafeSupportReference = SafeSupportReference(priorSession.CorrelationId),
                ActiveShift = ownState.ActiveShift,
                ActiveCashCustodySession = ownState.ActiveCashCustodySession
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return _lastState = locked;
        }
    }

    private async Task<HumanSessionSafeState> BuildSafeStateAsync(
        AptHumanSessionDto session,
        string message,
        CancellationToken cancellationToken,
        string? errorCode = null)
    {
        var ownState = await LoadOwnOperationalStateAsync(session.UserReference, cancellationToken).ConfigureAwait(false);
        var terminalState = await LoadTerminalOperationalStateAsync(cancellationToken).ConfigureAwait(false);
        var crossCashierShift = terminalState.ActiveShift is not null
            && !string.Equals(terminalState.ActiveShift.CashierId, session.UserReference.ToString("D"), StringComparison.OrdinalIgnoreCase);
        var crossCashierCustody = terminalState.ActiveCashCustodySession is not null
            && !string.Equals(terminalState.ActiveCashCustodySession.CashierId, session.UserReference.ToString("D"), StringComparison.OrdinalIgnoreCase);
        var shiftAuthorized = HasPermission(session, HumanSessionRuntimeOptions.ShiftOperatePermission) && !crossCashierShift && !crossCashierCustody;
        var custodyAuthorized = HasPermission(session, HumanSessionRuntimeOptions.CustodyOperatePermission) && !crossCashierShift && !crossCashierCustody;
        var cashAuthorized = shiftAuthorized
            && custodyAuthorized
            && HasPermission(session, HumanSessionRuntimeOptions.CashReceivePermission);
        var permissionErrorCode = !shiftAuthorized
            ? "SHIFT_PERMISSION_DENIED"
            : !custodyAuthorized
                ? "CUSTODY_PERMISSION_DENIED"
                : !cashAuthorized
                    ? "CASH_RECEIVE_PERMISSION_DENIED"
                    : null;
        var permissionMessage = permissionErrorCode switch
        {
            "SHIFT_PERMISSION_DENIED" => "This cashier is not currently authorized to operate a cashier shift.",
            "CUSTODY_PERMISSION_DENIED" => "This cashier is not currently authorized to operate cash custody.",
            "CASH_RECEIVE_PERMISSION_DENIED" => "This cashier is not currently authorized to receive cash at this terminal.",
            _ => message
        };

        return new HumanSessionSafeState(
            AuthenticationState: "AUTHENTICATED",
            Authenticated: true,
            DeviceTrusted: true,
            ShiftOperationsAuthorized: shiftAuthorized,
            CustodyOperationsAuthorized: custodyAuthorized,
            CashOperationsAuthorized: cashAuthorized,
            UserReference: session.UserReference.ToString("D"),
            Username: session.Username,
            DisplayName: session.DisplayName,
            Audience: session.Audience,
            Assurance: session.Assurance,
            PrivilegedAccount: session.PrivilegedAccount,
            MfaRequired: session.MfaRequired,
            IdleExpiresAt: session.IdleExpiresAt,
            AbsoluteExpiresAt: session.AbsoluteExpiresAt,
            SafeSupportReference: SafeSupportReference(session.CorrelationId),
            SafeMessage: crossCashierCustody
                ? "Another cashier retains open cash custody. This login cannot inherit it."
                : crossCashierShift
                    ? "Another cashier retains an open shift. This login cannot inherit it."
                    : errorCode is null ? permissionMessage : message,
            ErrorCode: crossCashierCustody
                ? "CROSS_CASHIER_CUSTODY_BLOCKED"
                : crossCashierShift
                    ? "CROSS_CASHIER_SHIFT_BLOCKED"
                    : errorCode ?? permissionErrorCode,
            Retryable: false,
            ActiveShift: ownState.ActiveShift,
            ActiveCashCustodySession: ownState.ActiveCashCustodySession);
    }

    private Task<LocalOperationalStateSnapshot> LoadOwnOperationalStateAsync(Guid userReference, CancellationToken cancellationToken) =>
        _journal.GetLocalOperationalStateAsync(new LocalOperationalStateRequest(
            CashierId: userReference.ToString("D"),
            TerminalId: _options!.TerminalId,
            SiteId: _options.SiteId.ToString("D"),
            SiteGroupId: _options.SiteGroupId.ToString("D"),
            PosServerId: _options.PosServerId), cancellationToken);

    private Task<LocalOperationalStateSnapshot> LoadTerminalOperationalStateAsync(CancellationToken cancellationToken) =>
        _journal.GetLocalOperationalStateAsync(new LocalOperationalStateRequest(
            TerminalId: _options!.TerminalId,
            SiteId: _options.SiteId.ToString("D"),
            SiteGroupId: _options.SiteGroupId.ToString("D"),
            PosServerId: _options.PosServerId), cancellationToken);

    private void ClearCredential()
    {
        _credential = null;
        _credentialStore.Delete();
    }

    private static string SafeSupportReference(Guid correlationId) =>
        correlationId == Guid.Empty ? "Unavailable" : $"APT-{correlationId:N}"[..12].ToUpperInvariant();

    private static bool IsTerminalSessionFailure(string? code) =>
        code is "SESSION_EXPIRED" or "SESSION_REVOKED" or "SESSION_INVALID" or "SESSION_NOT_FOUND";

    private static bool HasPermission(AptHumanSessionDto session, string permission) =>
        session.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
}

public sealed record HumanSessionSafeState(
    string AuthenticationState,
    bool Authenticated,
    bool DeviceTrusted,
    bool ShiftOperationsAuthorized,
    bool CustodyOperationsAuthorized,
    bool CashOperationsAuthorized,
    string? UserReference,
    string? Username,
    string? DisplayName,
    string? Audience,
    string? Assurance,
    bool PrivilegedAccount,
    bool MfaRequired,
    DateTimeOffset? IdleExpiresAt,
    DateTimeOffset? AbsoluteExpiresAt,
    string SafeSupportReference,
    string SafeMessage,
    string? ErrorCode,
    bool Retryable,
    CashierShiftSnapshot? ActiveShift,
    CashCustodySessionSnapshot? ActiveCashCustodySession)
{
    public static HumanSessionSafeState Unauthenticated(bool deviceTrusted, string message) =>
        new("UNAUTHENTICATED", false, deviceTrusted, false, false, false, null, null, null, null, null, false, false, null, null,
            "Unavailable", message, null, false, null, null);

    public static HumanSessionSafeState Unavailable(string code, string message, bool retryable) =>
        new("UNAVAILABLE", false, code != "APT_DEVICE_TRUST_UNAVAILABLE", false, false, false, null, null, null, null, null, false, false, null, null,
            "Unavailable", message, code, retryable, null, null);
}
