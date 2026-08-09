using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace AssistedPaymentTerminal.Desktop;

public static class HumanSessionBridgeCommand
{
    public const string Source = "apt-human-session";
    public const string Restore = "humanSession.restore";
    public const string Login = "humanSession.login";
    public const string Refresh = "humanSession.refresh";
    public const string Reauthenticate = "humanSession.reauthenticate";
    public const string Logout = "humanSession.logout";
    public const string OpenOrResumeShift = "humanSession.openOrResumeShift";
    public const string OpenOrResumeCustody = "humanSession.openOrResumeCustody";
    public const string AuthorizeCash = "humanSession.authorizeCash";
}

public sealed class HumanSessionBridgeHandler
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly HumanSessionRuntime _runtime;
    private readonly IHumanCredentialPrompt _credentialPrompt;
    private readonly HumanCredentialAttemptGate _credentialAttemptGate;
    private readonly HumanAuthenticationTrace _trace;
    private readonly SemaphoreSlim _credentialFlow = new(1, 1);

    public HumanSessionBridgeHandler(
        HumanSessionRuntime runtime,
        IHumanCredentialPrompt credentialPrompt,
        HumanCredentialAttemptGate? credentialAttemptGate = null,
        HumanAuthenticationTrace? trace = null)
    {
        _runtime = runtime;
        _credentialPrompt = credentialPrompt;
        _credentialAttemptGate = credentialAttemptGate ?? new HumanCredentialAttemptGate();
        _trace = trace ?? new HumanAuthenticationTrace();
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public async Task<string?> HandleWebMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        HumanSessionBridgeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<HumanSessionBridgeRequest>(message, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (request is null || !string.Equals(request.Source, HumanSessionBridgeCommand.Source, StringComparison.Ordinal))
        {
            return null;
        }
        if (string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            return Failure(request.Command, "", "MISSING_CORRELATION_ID", "Human-session commands require a correlation reference.");
        }

        try
        {
            _trace.Record(
                "bridge.command-received",
                request.Command == HumanSessionBridgeCommand.Login ? "LOGIN" : request.Command == HumanSessionBridgeCommand.Reauthenticate ? "REAUTHENTICATE" : "SESSION",
                nameof(HandleWebMessageAsync),
                request.Command,
                request.Command is HumanSessionBridgeCommand.Login or HumanSessionBridgeCommand.Reauthenticate,
                hostCorrelationId: request.CorrelationId);
            var state = request.Command switch
            {
                HumanSessionBridgeCommand.Restore => await _runtime.RestoreAsync(cancellationToken).ConfigureAwait(false),
                HumanSessionBridgeCommand.Login => await LoginAsync(request, cancellationToken).ConfigureAwait(false),
                HumanSessionBridgeCommand.Refresh => await _runtime.RefreshAsync(cancellationToken).ConfigureAwait(false),
                HumanSessionBridgeCommand.Reauthenticate => await ReauthenticateAsync(request, cancellationToken).ConfigureAwait(false),
                HumanSessionBridgeCommand.Logout => await _runtime.LogoutAsync(cancellationToken).ConfigureAwait(false),
                HumanSessionBridgeCommand.OpenOrResumeShift => await _runtime.OpenOrResumeShiftAsync(cancellationToken).ConfigureAwait(false),
                HumanSessionBridgeCommand.OpenOrResumeCustody => await OpenCustodyAsync(request, cancellationToken).ConfigureAwait(false),
                HumanSessionBridgeCommand.AuthorizeCash => await AuthorizeCashAsync(cancellationToken).ConfigureAwait(false),
                _ => null
            };
            if (state is not null
                && !state.Authenticated
                && request.Command is not HumanSessionBridgeCommand.Login
                && request.Command is not HumanSessionBridgeCommand.Reauthenticate)
            {
                InvalidateCredentialFlow("AUTHORITY_NOT_CURRENT");
            }
            return state is null
                ? Failure(request.Command, request.CorrelationId, "UNSUPPORTED_COMMAND", "Unsupported human-session command.")
                : Success(request.Command, request.CorrelationId, state);
        }
        catch (JsonException)
        {
            return Failure(request.Command, request.CorrelationId, "MALFORMED_REQUEST", "The human-session command could not be processed safely.");
        }
        catch (HumanCredentialPromptException exception)
        {
            return Failure(request.Command, request.CorrelationId, exception.Code, exception.SafeMessage);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            return Failure(request.Command, request.CorrelationId, "OS_CREDENTIAL_PROTECTION_UNAVAILABLE", "Windows credential protection is unavailable. Cashier authority remains locked.");
        }
        catch (Exception)
        {
            return Failure(request.Command, request.CorrelationId, "HUMAN_SESSION_UNAVAILABLE", "Online cashier authority could not be confirmed. New cash remains locked.");
        }
    }

    private async Task<HumanSessionSafeState> LoginAsync(HumanSessionBridgeRequest request, CancellationToken cancellationToken)
    {
        EnsureExactPayload(request.Payload, "username");
        var payload = request.Payload.Deserialize<LoginPayload>(JsonOptions)
            ?? throw new JsonException();
        if (string.IsNullOrWhiteSpace(payload.Username))
        {
            throw new JsonException();
        }

        if (_runtime.CurrentState.Authenticated)
        {
            throw new HumanCredentialPromptException("LOGIN_NOT_REQUIRED", "Current cashier authority is already established.");
        }

        return await RunExplicitCredentialOperationAsync(
            HumanCredentialOperation.Login,
            payload.Username.Trim(),
            request.CorrelationId,
            credential => _runtime.LoginAsync(payload.Username, credential, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanSessionSafeState> ReauthenticateAsync(HumanSessionBridgeRequest request, CancellationToken cancellationToken)
    {
        EnsureExactPayload(request.Payload);
        if (!_runtime.CurrentState.Authenticated)
        {
            throw new HumanCredentialPromptException("SIGN_IN_REQUIRED", "Sign in again to continue.");
        }

        return await RunExplicitCredentialOperationAsync(
            HumanCredentialOperation.Reauthenticate,
            _runtime.CurrentState.Username,
            request.CorrelationId,
            credential => _runtime.ReauthenticateAsync(credential, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanSessionSafeState> RunExplicitCredentialOperationAsync(
        HumanCredentialOperation operation,
        string? username,
        string hostCorrelationId,
        Func<ExplicitHumanCredentialSubmission, Task<HumanSessionSafeState>> execute,
        CancellationToken cancellationToken)
    {
        if (!await _credentialFlow.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new HumanCredentialPromptException(
                "CREDENTIAL_ENTRY_IN_PROGRESS",
                "A cashier credential entry is already in progress.");
        }

        try
        {
            var credential = await GetExplicitCredentialAsync(
                operation,
                username,
                hostCorrelationId,
                cancellationToken).ConfigureAwait(false);
            return await execute(credential).ConfigureAwait(false);
        }
        finally
        {
            _credentialFlow.Release();
        }
    }

    private async Task<ExplicitHumanCredentialSubmission> GetExplicitCredentialAsync(
        HumanCredentialOperation operation,
        string? username,
        string hostCorrelationId,
        CancellationToken cancellationToken)
    {
        var authorityVersion = _runtime.AuthorityVersion;
        if (!_credentialAttemptGate.TryBegin(operation, authorityVersion, out var attempt))
        {
            throw new HumanCredentialPromptException(
                "CREDENTIAL_ENTRY_IN_PROGRESS",
                "A cashier credential entry is already in progress.");
        }
        _trace.Record("credential-attempt.created", operation.ToString().ToUpperInvariant(), nameof(GetExplicitCredentialAsync), "BRIDGE_EXPLICIT_COMMAND", true, attempt.AttemptReference, hostCorrelationId);
        HumanCredentialPromptResult result;
        try
        {
            result = await _credentialPrompt.PromptAsync(
                new HumanCredentialPromptRequest(attempt.AttemptReference, operation, username, hostCorrelationId),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _credentialAttemptGate.Invalidate(attempt.AttemptReference);
            _trace.Record("credential-attempt.invalidated", operation.ToString().ToUpperInvariant(), nameof(GetExplicitCredentialAsync), "PROMPT_EXCEPTION", false, attempt.AttemptReference, hostCorrelationId);
            throw;
        }

        if (!result.Accepted)
        {
            _credentialAttemptGate.Invalidate(attempt.AttemptReference);
            _trace.Record("credential-attempt.invalidated", operation.ToString().ToUpperInvariant(), nameof(GetExplicitCredentialAsync), "PROMPT_CANCELLED", false, attempt.AttemptReference, hostCorrelationId);
            throw new HumanCredentialPromptException(
                "CREDENTIAL_ENTRY_CANCELLED",
                "Fresh cashier credential entry was cancelled. No authentication request was sent.");
        }
        var credential = _credentialAttemptGate.TryConsume(
            result,
            operation,
            _runtime.AuthorityVersion,
            hostCorrelationId);
        if (credential is null)
        {
            _credentialAttemptGate.Invalidate(attempt.AttemptReference);
            _trace.Record("credential-attempt.invalidated", operation.ToString().ToUpperInvariant(), nameof(GetExplicitCredentialAsync), "PROMPT_RESULT_REJECTED", false, attempt.AttemptReference, hostCorrelationId);
            throw new HumanCredentialPromptException(
                "EXPLICIT_CREDENTIAL_ENTRY_REQUIRED",
                "Fresh cashier credential entry is required. No authentication request was sent.");
        }

        _trace.Record("credential-attempt.consumed", operation.ToString().ToUpperInvariant(), nameof(GetExplicitCredentialAsync), result.SubmitTrigger, true, attempt.AttemptReference, hostCorrelationId);
        return credential;
    }

    private void InvalidateCredentialFlow(string reason)
    {
        _credentialAttemptGate.InvalidateAll();
        _credentialPrompt.CancelActive(reason);
        _trace.Record("credential-flow.invalidated", sourceMethod: nameof(InvalidateCredentialFlow), sourceTrigger: reason, explicitUserAction: false);
    }

    private static void EnsureExactPayload(JsonElement payload, params string[] allowedProperties)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException();
        }

        var allowed = allowedProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in payload.EnumerateObject())
        {
            if (!allowed.Remove(property.Name))
            {
                throw new JsonException();
            }
        }
        if (allowed.Count != 0)
        {
            throw new JsonException();
        }
    }

    private async Task<HumanSessionSafeState> OpenCustodyAsync(HumanSessionBridgeRequest request, CancellationToken cancellationToken)
    {
        var payload = request.Payload.Deserialize<OpenCustodyPayload>(JsonOptions)
            ?? throw new JsonException();
        return await _runtime.OpenOrResumeCustodyAsync(payload.OpeningCashAmount, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanSessionSafeState> AuthorizeCashAsync(CancellationToken cancellationToken)
    {
        var authorization = await _runtime.AuthorizeCashAsync(cancellationToken).ConfigureAwait(false);
        var state = await _runtime.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return authorization.Authorized
            ? state
            : state with { CashOperationsAuthorized = false, ErrorCode = authorization.Code, SafeMessage = authorization.SafeMessage };
    }

    private static string Success(string command, string correlationId, HumanSessionSafeState payload) =>
        JsonSerializer.Serialize(new { source = HumanSessionBridgeCommand.Source, ok = true, command, correlationId, payload }, JsonOptions);

    private static string Failure(string command, string correlationId, string code, string message) =>
        JsonSerializer.Serialize(new { source = HumanSessionBridgeCommand.Source, ok = false, command, correlationId, error = new { code, message } }, JsonOptions);
}

public sealed record HumanSessionBridgeRequest(string Source, string Command, string CorrelationId, JsonElement Payload);
public sealed record LoginPayload(string Username);
public sealed record OpenCustodyPayload(decimal OpeningCashAmount);

public sealed class HumanCredentialPromptException : Exception
{
    public HumanCredentialPromptException(string code, string safeMessage)
        : base(safeMessage)
    {
        Code = code;
        SafeMessage = safeMessage;
    }

    public string Code { get; }
    public string SafeMessage { get; }
}
