using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.Desktop;

public sealed partial class HumanSessionRuntime
{
    public async Task<HumanSessionSafeState> InitiateCashCustodyMovementAsync(
        CashCustodyMovementType movementType,
        Guid? originalMovementId,
        string intendedCounterpartyReference,
        string externalHandoverReference,
        string idempotencyKey,
        string correlationId,
        IReadOnlyCollection<CashCustodyDenominationLine> denominations,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.Authenticated || _session is null || _options is null)
            {
                return refreshed;
            }
            if (!HasPermission(_session, HumanSessionRuntimeOptions.MovementInitiatePermission))
            {
                return _lastState = refreshed with
                {
                    MovementInitiationAuthorized = false,
                    ErrorCode = "MOVEMENT_INITIATE_PERMISSION_DENIED",
                    SafeMessage = "This cashier is not assigned cash-custody movement authority."
                };
            }

            var ownState = await LoadOwnOperationalStateAsync(_session.UserReference, cancellationToken).ConfigureAwait(false);
            if (ownState.ActiveShift is null || ownState.ActiveCashCustodySession is null)
            {
                return _lastState = refreshed with
                {
                    ErrorCode = "OWN_CUSTODY_REQUIRED",
                    SafeMessage = "An owned open cash-custody session is required."
                };
            }

            var result = await _journal.InitiateCashCustodyMovementAsync(new InitiateCashCustodyMovementRequest(
                ownState.ActiveCashCustodySession.Id,
                movementType,
                originalMovementId,
                _session.UserReference.ToString("D"),
                _session.SessionReference.ToString("D"),
                ownState.ActiveShift.Id,
                _options.TerminalId,
                _options.SiteId.ToString("D"),
                _options.SiteGroupId.ToString("D"),
                _options.PosServerId,
                intendedCounterpartyReference,
                externalHandoverReference,
                idempotencyKey,
                correlationId,
                denominations), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? _lastState = await BuildSafeStateAsync(_session, "Cash movement is pending independent acknowledgment.", cancellationToken).ConfigureAwait(false)
                : await MovementFailureStateAsync(result.Error!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> CancelCashCustodyMovementAsync(
        Guid movementId,
        string reasonCode,
        string idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.Authenticated || _session is null || _options is null)
            {
                return refreshed;
            }
            if (!HasPermission(_session, HumanSessionRuntimeOptions.MovementInitiatePermission))
            {
                return _lastState = refreshed with { ErrorCode = "MOVEMENT_INITIATE_PERMISSION_DENIED", SafeMessage = "Movement cancellation is not authorized." };
            }

            var result = await _journal.ResolvePendingCashCustodyMovementAsync(new ResolvePendingCashCustodyMovementRequest(
                movementId,
                CashCustodyMovementEventType.Cancelled,
                _session.UserReference.ToString("D"),
                _session.SessionReference.ToString("D"),
                _options.TerminalId,
                _options.SiteId.ToString("D"),
                _options.SiteGroupId.ToString("D"),
                _options.PosServerId,
                idempotencyKey,
                correlationId,
                reasonCode), cancellationToken).ConfigureAwait(false);
            return result.IsSuccess
                ? _lastState = await BuildSafeStateAsync(_session, "Pending cash movement cancelled.", cancellationToken).ConfigureAwait(false)
                : await MovementFailureStateAsync(result.Error!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> AcknowledgeOrRejectCashCustodyMovementAsync(
        Guid movementId,
        string counterpartyUsername,
        bool reject,
        string? reasonCode,
        string idempotencyKey,
        string correlationId,
        ExplicitHumanCredentialSubmission credentialSubmission,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(counterpartyUsername)
                || !credentialSubmission.TryConsume(HumanCredentialOperation.MovementCounterpartyAuthentication, AuthorityVersion, out var password))
            {
                return _lastState = _lastState with { ErrorCode = "INVALID_COUNTERPARTY_AUTHENTICATION", SafeMessage = "Enter the authorized counterparty credentials." };
            }

            var refreshed = await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.Authenticated || _session is null || _options is null)
            {
                return refreshed;
            }

            var cashierSession = _session;
            var loginCorrelation = Guid.NewGuid();
            var login = await _client.LoginAsync(counterpartyUsername.Trim(), password, _options.SiteId, loginCorrelation, cancellationToken).ConfigureAwait(false);
            if (!TryResolveTemporaryPrincipal(login, cashierSession, HumanSessionRuntimeOptions.MovementAcknowledgePermission, out var principal, out var token, out var failure))
            {
                return _lastState = refreshed with { ErrorCode = failure.Code, SafeMessage = failure.Message };
            }

            var temporarySessionReference = principal!.SessionReference;
            var temporaryToken = token!;

            try
            {
                var revalidated = await _client.GetAsync(temporarySessionReference, temporaryToken, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                if (!TryResolveTemporaryPrincipal(revalidated, cashierSession, HumanSessionRuntimeOptions.MovementAcknowledgePermission, out principal, out _, out failure, temporaryToken))
                {
                    return _lastState = refreshed with { ErrorCode = failure.Code, SafeMessage = failure.Message };
                }

                CashJournalResult<CashCustodyMovementSnapshot> result;
                if (reject)
                {
                    if (string.IsNullOrWhiteSpace(reasonCode))
                    {
                        return _lastState = refreshed with { ErrorCode = "MOVEMENT_REJECTION_REASON_REQUIRED", SafeMessage = "A rejection reason is required." };
                    }
                    result = await _journal.ResolvePendingCashCustodyMovementAsync(new ResolvePendingCashCustodyMovementRequest(
                        movementId,
                        CashCustodyMovementEventType.Rejected,
                        principal!.UserReference.ToString("D"),
                        principal.SessionReference.ToString("D"),
                        _options.TerminalId,
                        _options.SiteId.ToString("D"),
                        _options.SiteGroupId.ToString("D"),
                        _options.PosServerId,
                        idempotencyKey,
                        correlationId,
                        reasonCode.Trim()), cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await _journal.AcknowledgeCashCustodyMovementAsync(new AcknowledgeCashCustodyMovementRequest(
                        movementId,
                        cashierSession.UserReference.ToString("D"),
                        principal!.UserReference.ToString("D"),
                        principal.Username,
                        principal.SessionReference.ToString("D"),
                        _options.TerminalId,
                        _options.SiteId.ToString("D"),
                        _options.SiteGroupId.ToString("D"),
                        _options.PosServerId,
                        idempotencyKey,
                        correlationId), cancellationToken).ConfigureAwait(false);
                }

                return result.IsSuccess
                    ? _lastState = await BuildSafeStateAsync(cashierSession, reject ? "Cash movement rejected." : "Cash movement handover completed.", cancellationToken).ConfigureAwait(false)
                    : await MovementFailureStateAsync(result.Error!, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _client.LogoutAsync(temporarySessionReference, temporaryToken, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<HumanSessionSafeState> AppendCashCustodyMovementNoteAsync(
        Guid movementId,
        CashCustodyMovementEventType noteType,
        string counterpartyUsername,
        string reasonCode,
        string controlledMetadata,
        string idempotencyKey,
        string correlationId,
        ExplicitHumanCredentialSubmission credentialSubmission,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(counterpartyUsername)
                || !credentialSubmission.TryConsume(HumanCredentialOperation.MovementCounterpartyAuthentication, AuthorityVersion, out var password))
            {
                return _lastState = _lastState with { ErrorCode = "INVALID_COUNTERPARTY_AUTHENTICATION", SafeMessage = "Enter an authorized correcting principal's credentials." };
            }

            var refreshed = await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.Authenticated || _session is null || _options is null)
            {
                return refreshed;
            }
            var cashierSession = _session;
            var login = await _client.LoginAsync(counterpartyUsername.Trim(), password, _options.SiteId, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
            if (!TryResolveTemporaryPrincipal(login, cashierSession, HumanSessionRuntimeOptions.MovementCorrectPermission, out var principal, out var token, out var failure))
            {
                return _lastState = refreshed with { ErrorCode = failure.Code, SafeMessage = failure.Message };
            }
            var temporarySessionReference = principal!.SessionReference;
            var temporaryToken = token!;
            try
            {
                var revalidated = await _client.GetAsync(temporarySessionReference, temporaryToken, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                if (!TryResolveTemporaryPrincipal(revalidated, cashierSession, HumanSessionRuntimeOptions.MovementCorrectPermission, out principal, out _, out failure, temporaryToken))
                {
                    return _lastState = refreshed with { ErrorCode = failure.Code, SafeMessage = failure.Message };
                }
                var result = await _journal.AppendCashCustodyMovementNoteAsync(new AppendCashCustodyMovementNoteRequest(
                    movementId,
                    noteType,
                    principal!.UserReference.ToString("D"),
                    principal.SessionReference.ToString("D"),
                    _options.TerminalId,
                    _options.SiteId.ToString("D"),
                    _options.SiteGroupId.ToString("D"),
                    _options.PosServerId,
                    idempotencyKey,
                    correlationId,
                    reasonCode,
                    controlledMetadata), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess
                    ? _lastState = await BuildSafeStateAsync(cashierSession, "Movement note appended without changing financial evidence.", cancellationToken).ConfigureAwait(false)
                    : await MovementFailureStateAsync(result.Error!, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await _client.LogoutAsync(temporarySessionReference, temporaryToken, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private bool TryResolveTemporaryPrincipal(
        HumanSessionClientResult result,
        AptHumanSessionDto cashierSession,
        string requiredPermission,
        out AptHumanSessionDto? principal,
        out string? token,
        out (string Code, string Message) failure,
        string? existingToken = null)
    {
        principal = result.Response?.Session;
        token = string.IsNullOrWhiteSpace(result.Response?.AptSessionToken)
            ? existingToken
            : result.Response.AptSessionToken;
        if (!result.Ok || principal is null || string.IsNullOrWhiteSpace(token))
        {
            failure = (result.ErrorCode ?? "COUNTERPARTY_AUTHENTICATION_FAILED", result.SafeMessage ?? "The authorized counterparty could not be authenticated.");
            return false;
        }
        var validation = ValidateSession(principal);
        if (!validation.Valid)
        {
            failure = (validation.Code, validation.Message);
            return false;
        }
        if (principal.UserReference == cashierSession.UserReference)
        {
            failure = ("DISTINCT_PRINCIPAL_REQUIRED", "The receiving party must be a different authenticated person.");
            return false;
        }
        if (!HasPermission(principal, requiredPermission))
        {
            failure = ("MOVEMENT_PERMISSION_DENIED", "The authenticated person is not authorized for this cash movement action.");
            return false;
        }
        failure = ("AUTHORIZED", "Authorized.");
        return true;
    }

    private async Task<HumanSessionSafeState> MovementFailureStateAsync(CashJournalError error, CancellationToken cancellationToken)
    {
        var state = _session is null
            ? _lastState
            : await BuildSafeStateAsync(_session, error.Message, cancellationToken, error.Code.ToString().ToUpperInvariant()).ConfigureAwait(false);
        return _lastState = state;
    }
}
