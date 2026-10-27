using AssistedPaymentTerminal.LocalOperations;

namespace AssistedPaymentTerminal.Desktop;

public sealed partial class HumanSessionRuntime
{
    public async Task<HumanSessionSafeState> ListCashCustodyMovementCounterpartiesAsync(
        CashCustodyMovementType movementType,
        Guid? originalMovementId,
        string? requiredCounterpartyReference,
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
                    MovementCounterparties = Array.Empty<CashCustodyCounterpartyOption>(),
                    MovementCounterpartyContext = CounterpartyContext(movementType, originalMovementId, requiredCounterpartyReference),
                    ErrorCode = "MOVEMENT_COUNTERPARTY_DIRECTORY_DENIED",
                    SafeMessage = "Receiving-person selection requires cash-custody movement authority."
                };
            }

            var directory = await QueryCounterpartiesLockedAsync(
                refreshed,
                movementType,
                originalMovementId,
                requiredCounterpartyReference,
                cancellationToken).ConfigureAwait(false);
            if (!directory.Ok)
            {
                return _lastState = refreshed with
                {
                    MovementCounterparties = Array.Empty<CashCustodyCounterpartyOption>(),
                    MovementCounterpartyContext = CounterpartyContext(movementType, originalMovementId, requiredCounterpartyReference),
                    ErrorCode = directory.ErrorCode,
                    SafeMessage = directory.SafeMessage ?? "Authorized receiving persons are unavailable."
                };
            }

            return _lastState = refreshed with
            {
                MovementCounterparties = directory.Candidates.Select(ToOption).ToArray(),
                MovementCounterpartyContext = CounterpartyContext(movementType, originalMovementId, requiredCounterpartyReference),
                ErrorCode = null,
                SafeMessage = directory.Candidates.Count == 0
                    ? "No authorized receiving person is currently available for this movement."
                    : "Authorized receiving persons refreshed from Central PMS."
            };
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<MovementCounterpartyPromptPreparation> PrepareCashCustodyMovementCounterpartyAsync(
        Guid movementId,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var refreshed = await RefreshLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!refreshed.Authenticated || _session is null || _options is null)
            {
                return new(refreshed, null);
            }

            var movement = refreshed.ActiveCashCustodySummary?.Movements?.SingleOrDefault(value => value.Id == movementId);
            if (movement is null || movement.Status != CashCustodyMovementStatus.PendingAcknowledgment)
            {
                return new(_lastState = refreshed with
                {
                    ErrorCode = "PENDING_MOVEMENT_NOT_FOUND",
                    SafeMessage = "The pending movement could not be resolved for receiver authentication."
                }, null);
            }

            var directory = await QueryCounterpartiesLockedAsync(
                refreshed,
                movement.MovementType,
                movement.OriginalMovementId,
                movement.IntendedCounterpartyReference,
                cancellationToken).ConfigureAwait(false);
            var candidate = directory.Candidates.SingleOrDefault();
            if (!directory.Ok || candidate is null)
            {
                return new(_lastState = refreshed with
                {
                    ErrorCode = directory.ErrorCode ?? "MOVEMENT_COUNTERPARTY_NO_LONGER_AUTHORIZED",
                    SafeMessage = directory.SafeMessage ?? "The designated receiving person is no longer eligible to acknowledge this movement."
                }, null);
            }

            return new(refreshed, ToOption(candidate));
        }
        finally
        {
            _mutex.Release();
        }
    }

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

            var selected = await QueryCounterpartiesLockedAsync(
                refreshed,
                movementType,
                originalMovementId,
                intendedCounterpartyReference,
                cancellationToken).ConfigureAwait(false);
            var selectedCandidate = selected.Candidates.SingleOrDefault();
            if (!selected.Ok
                || selectedCandidate is null
                || !string.Equals(selectedCandidate.UserReference.ToString("D"), intendedCounterpartyReference.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return _lastState = refreshed with
                {
                    ErrorCode = selected.ErrorCode ?? "MOVEMENT_COUNTERPARTY_NOT_ELIGIBLE",
                    SafeMessage = selected.SafeMessage ?? "Select a currently authorized receiving person."
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
        Guid expectedCounterpartyUserReference,
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
            if (expectedCounterpartyUserReference == Guid.Empty
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
            var movement = refreshed.ActiveCashCustodySummary?.Movements?.SingleOrDefault(value => value.Id == movementId);
            if (movement is null || movement.Status != CashCustodyMovementStatus.PendingAcknowledgment)
            {
                return _lastState = refreshed with { ErrorCode = "PENDING_MOVEMENT_NOT_FOUND", SafeMessage = "The pending movement is no longer available." };
            }
            var directory = await QueryCounterpartiesLockedAsync(
                refreshed,
                movement.MovementType,
                movement.OriginalMovementId,
                movement.IntendedCounterpartyReference,
                cancellationToken).ConfigureAwait(false);
            var designated = directory.Candidates.SingleOrDefault();
            if (!directory.Ok || designated is null || designated.UserReference != expectedCounterpartyUserReference)
            {
                return _lastState = refreshed with
                {
                    ErrorCode = directory.ErrorCode ?? "MOVEMENT_COUNTERPARTY_NO_LONGER_AUTHORIZED",
                    SafeMessage = directory.SafeMessage ?? "The designated receiving person is no longer eligible to acknowledge this movement."
                };
            }
            var loginCorrelation = Guid.NewGuid();
            var login = await _client.LoginAsync(designated.LoginIdentifier, password, _options.SiteId, loginCorrelation, cancellationToken).ConfigureAwait(false);
            if (!TryResolveTemporaryPrincipal(login, cashierSession, HumanSessionRuntimeOptions.MovementAcknowledgePermission, designated.UserReference, out var principal, out var token, out var failure))
            {
                return _lastState = refreshed with { ErrorCode = failure.Code, SafeMessage = failure.Message };
            }

            var temporarySessionReference = principal!.SessionReference;
            var temporaryToken = token!;

            try
            {
                var revalidated = await _client.GetAsync(temporarySessionReference, temporaryToken, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                if (!TryResolveTemporaryPrincipal(revalidated, cashierSession, HumanSessionRuntimeOptions.MovementAcknowledgePermission, designated.UserReference, out principal, out _, out failure, temporaryToken))
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
            if (!TryResolveTemporaryPrincipal(login, cashierSession, HumanSessionRuntimeOptions.MovementCorrectPermission, null, out var principal, out var token, out var failure))
            {
                return _lastState = refreshed with { ErrorCode = failure.Code, SafeMessage = failure.Message };
            }
            var temporarySessionReference = principal!.SessionReference;
            var temporaryToken = token!;
            try
            {
                var revalidated = await _client.GetAsync(temporarySessionReference, temporaryToken, Guid.NewGuid(), cancellationToken).ConfigureAwait(false);
                if (!TryResolveTemporaryPrincipal(revalidated, cashierSession, HumanSessionRuntimeOptions.MovementCorrectPermission, null, out principal, out _, out failure, temporaryToken))
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
        Guid? expectedUserReference,
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
        if (expectedUserReference.HasValue && principal.UserReference != expectedUserReference.Value)
        {
            failure = ("DESIGNATED_RECEIVER_MISMATCH", "The authenticated person is not the designated receiving person for this movement.");
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

    private async Task<CounterpartyDirectoryResolution> QueryCounterpartiesLockedAsync(
        HumanSessionSafeState state,
        CashCustodyMovementType movementType,
        Guid? originalMovementId,
        string? requiredCounterpartyReference,
        CancellationToken cancellationToken)
    {
        if (_session is null || _credential is null || _options is null)
        {
            return CounterpartyDirectoryResolution.Failure("SESSION_REQUIRED", "Cashier authentication is required.");
        }

        string? originalMovementType = null;
        string? governedRequiredReference = requiredCounterpartyReference;
        if (movementType == CashCustodyMovementType.CashReturn)
        {
            var original = state.ActiveCashCustodySummary?.Movements?.SingleOrDefault(value => value.Id == originalMovementId);
            var completion = original?.Events.SingleOrDefault(value => value.EventType == CashCustodyMovementEventType.Completed);
            if (original is null
                || original.MovementType is not (CashCustodyMovementType.SafeDrop or CashCustodyMovementType.DirectRemittance)
                || original.Status != CashCustodyMovementStatus.Completed
                || completion is null)
            {
                return CounterpartyDirectoryResolution.Failure("INVALID_ORIGINAL_MOVEMENT", "Select an eligible completed transfer for the cash return.");
            }
            originalMovementType = original.MovementType.ToString();
            governedRequiredReference = completion.AuthorizedActorId;
        }

        var response = await _client.ListCashCustodyMovementCounterpartiesAsync(
            _session.SessionReference,
            _credential.SessionToken,
            _options.SiteId,
            movementType.ToString(),
            originalMovementType,
            governedRequiredReference,
            Guid.NewGuid(),
            cancellationToken).ConfigureAwait(false);
        if (!response.Ok || response.Response is null)
        {
            return CounterpartyDirectoryResolution.Failure(
                response.ErrorCode ?? "MOVEMENT_COUNTERPARTY_DIRECTORY_UNAVAILABLE",
                response.SafeMessage ?? "Authorized receiving persons are unavailable.");
        }

        var candidates = response.Response.Counterparties;
        if (movementType == CashCustodyMovementType.CashReturn
            && !string.IsNullOrWhiteSpace(requiredCounterpartyReference)
            && candidates.SingleOrDefault() is { } governed
            && !string.Equals(requiredCounterpartyReference, governed.UserReference.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && !string.Equals(requiredCounterpartyReference, governed.LoginIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return CounterpartyDirectoryResolution.Failure("INVALID_RETURN_COUNTERPARTY", "A cash return must use the receiving party recorded on the original transfer.");
        }
        return CounterpartyDirectoryResolution.Success(candidates);
    }

    private static CashCustodyCounterpartyOption ToOption(AptCashCustodyCounterpartyDto value) =>
        new(value.UserReference.ToString("D"), value.DisplayName, value.RoleCode, value.RoleLabel);

    private static string CounterpartyContext(CashCustodyMovementType movementType, Guid? originalMovementId, string? requiredCounterpartyReference) =>
        $"{movementType}:{originalMovementId?.ToString("D") ?? "none"}:{requiredCounterpartyReference?.Trim() ?? "all"}";

    private async Task<HumanSessionSafeState> MovementFailureStateAsync(CashJournalError error, CancellationToken cancellationToken)
    {
        var state = _session is null
            ? _lastState
            : await BuildSafeStateAsync(_session, error.Message, cancellationToken, error.Code.ToString().ToUpperInvariant()).ConfigureAwait(false);
        return _lastState = state;
    }
}

public sealed record CashCustodyCounterpartyOption(
    string UserReference,
    string DisplayName,
    string RoleCode,
    string RoleLabel);

public sealed record MovementCounterpartyPromptPreparation(
    HumanSessionSafeState State,
    CashCustodyCounterpartyOption? Counterparty);

internal sealed record CounterpartyDirectoryResolution(
    bool Ok,
    IReadOnlyList<AptCashCustodyCounterpartyDto> Candidates,
    string? ErrorCode,
    string? SafeMessage)
{
    public static CounterpartyDirectoryResolution Success(IReadOnlyList<AptCashCustodyCounterpartyDto> candidates) =>
        new(true, candidates, null, null);

    public static CounterpartyDirectoryResolution Failure(string code, string message) =>
        new(false, Array.Empty<AptCashCustodyCounterpartyDto>(), code, message);
}
