using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace AssistedPaymentTerminal.Desktop;

public enum HumanCredentialOperation
{
    Login,
    Reauthenticate
}

public sealed record HumanCredentialAttempt(
    Guid AttemptReference,
    HumanCredentialOperation Operation,
    long AuthorityVersion,
    DateTimeOffset ExpiresAt);

public sealed class ExplicitHumanCredentialSubmission : IDisposable
{
    private string? _credentialValue;
    private int _consumed;

    internal ExplicitHumanCredentialSubmission(
        Guid attemptReference,
        HumanCredentialOperation operation,
        long authorityVersion,
        string hostCorrelationId,
        string credentialValue)
    {
        AttemptReference = attemptReference;
        Operation = operation;
        AuthorityVersion = authorityVersion;
        HostCorrelationId = hostCorrelationId;
        _credentialValue = credentialValue;
    }

    public Guid AttemptReference { get; }
    public HumanCredentialOperation Operation { get; }
    public long AuthorityVersion { get; }
    public string HostCorrelationId { get; }

    internal bool TryConsume(HumanCredentialOperation operation, long authorityVersion, out string credentialValue)
    {
        credentialValue = string.Empty;
        if (operation != Operation
            || authorityVersion != AuthorityVersion
            || Interlocked.Exchange(ref _consumed, 1) != 0)
        {
            Dispose();
            return false;
        }

        credentialValue = Interlocked.Exchange(ref _credentialValue, null) ?? string.Empty;
        return credentialValue.Length != 0;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _credentialValue, null);
        Interlocked.Exchange(ref _consumed, 1);
    }
}

public sealed record HumanCredentialPromptRequest(
    Guid AttemptReference,
    HumanCredentialOperation Operation,
    string? Username,
    string HostCorrelationId);

public sealed record HumanCredentialPromptResult(
    Guid AttemptReference,
    bool Accepted,
    string? Password,
    string SubmitTrigger);

public interface IHumanCredentialPrompt
{
    Task<HumanCredentialPromptResult> PromptAsync(
        HumanCredentialPromptRequest request,
        CancellationToken cancellationToken = default);

    void CancelActive(string reason);
}

public sealed class HumanCredentialAttemptGate
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private HumanCredentialAttempt? _current;

    public HumanCredentialAttemptGate(TimeProvider? timeProvider = null, TimeSpan? lifetime = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetime = lifetime ?? TimeSpan.FromMinutes(2);
    }

    public bool TryBegin(HumanCredentialOperation operation, long authorityVersion, out HumanCredentialAttempt attempt)
    {
        lock (_sync)
        {
            if (_current is not null && _current.ExpiresAt >= _timeProvider.GetUtcNow())
            {
                attempt = _current;
                return false;
            }

            _current = attempt = new HumanCredentialAttempt(
                Guid.NewGuid(),
                operation,
                authorityVersion,
                _timeProvider.GetUtcNow().Add(_lifetime));
            return true;
        }
    }

    public ExplicitHumanCredentialSubmission? TryConsume(
        HumanCredentialPromptResult result,
        HumanCredentialOperation operation,
        long authorityVersion,
        string hostCorrelationId)
    {
        lock (_sync)
        {
            var attempt = _current;
            if (attempt is null || attempt.AttemptReference != result.AttemptReference)
            {
                return null;
            }

            _current = null;
            if (attempt.Operation != operation
                || attempt.AuthorityVersion != authorityVersion
                || attempt.ExpiresAt < _timeProvider.GetUtcNow()
                || !result.Accepted
                || !string.Equals(result.SubmitTrigger, "NATIVE_EXPLICIT_SUBMIT", StringComparison.Ordinal)
                || string.IsNullOrEmpty(result.Password))
            {
                return null;
            }

            return new ExplicitHumanCredentialSubmission(
                attempt.AttemptReference,
                attempt.Operation,
                attempt.AuthorityVersion,
                hostCorrelationId,
                result.Password);
        }
    }

    public void Invalidate(Guid attemptReference)
    {
        lock (_sync)
        {
            if (_current?.AttemptReference == attemptReference)
            {
                _current = null;
            }
        }
    }

    public void InvalidateAll()
    {
        lock (_sync)
        {
            _current = null;
        }
    }
}

public sealed class WpfHumanCredentialPrompt : IHumanCredentialPrompt
{
    private readonly Window _owner;
    private readonly HumanAuthenticationTrace _trace;
    private readonly object _sync = new();
    private Window? _activeDialog;
    private Guid? _activeAttemptReference;
    private string? _activeHostCorrelationId;

    public WpfHumanCredentialPrompt(Window owner, HumanAuthenticationTrace? trace = null)
    {
        _owner = owner;
        _trace = trace ?? new HumanAuthenticationTrace();
    }

    public Task<HumanCredentialPromptResult> PromptAsync(
        HumanCredentialPromptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _owner.Dispatcher.CheckAccess()
            ? Task.FromResult(ShowPrompt(request, cancellationToken))
            : _owner.Dispatcher.InvokeAsync(() => ShowPrompt(request, cancellationToken)).Task;
    }

    public void CancelActive(string reason)
    {
        Window? dialog;
        Guid? attemptReference;
        string? hostCorrelationId;
        lock (_sync)
        {
            dialog = _activeDialog;
            attemptReference = _activeAttemptReference;
            hostCorrelationId = _activeHostCorrelationId;
        }
        if (dialog is null)
        {
            return;
        }

        _trace.Record(
            "prompt.cancellation-requested",
            sourceMethod: nameof(CancelActive),
            sourceTrigger: reason,
            explicitUserAction: false,
            attemptReference: attemptReference,
            hostCorrelationId: hostCorrelationId);
        _ = dialog.Dispatcher.InvokeAsync(() =>
        {
            if (dialog.IsVisible)
            {
                dialog.Close();
            }
        });
    }

    private HumanCredentialPromptResult ShowPrompt(
        HumanCredentialPromptRequest request,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_activeDialog is not null)
            {
                return new HumanCredentialPromptResult(request.AttemptReference, false, null, "PROMPT_ALREADY_ACTIVE");
            }
        }

        var accepted = false;
        var enteredAfterPresentation = false;
        string? submittedCredential = null;
        var submitTrigger = "CANCELLED";
        _trace.Record(
            "prompt.opened",
            request.Operation.ToString().ToUpperInvariant(),
            nameof(WpfHumanCredentialPrompt),
            "NATIVE_DIALOG",
            false,
            request.AttemptReference,
            request.HostCorrelationId);

        var passwordBox = new PasswordBox
        {
            Margin = new Thickness(0, 6, 0, 16),
            MinWidth = 320
        };
        AutomationProperties.SetName(passwordBox, "Cashier password");

        var acceptButton = new Button
        {
            Content = request.Operation == HumanCredentialOperation.Login ? "Sign in" : "Reauthenticate",
            IsDefault = true,
            IsEnabled = false,
            MinWidth = 120,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var cancelButton = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 90
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(acceptButton);
        buttons.Children.Add(cancelButton);

        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock
        {
            Text = request.Operation == HumanCredentialOperation.Login
                ? "Enter the cashier password to sign in to Central PMS."
                : "Enter the cashier password for fresh Central PMS authentication.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });
        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            content.Children.Add(new TextBlock
            {
                Text = $"Username: {request.Username}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }
        var label = new Label { Content = "Password", Target = passwordBox, Padding = new Thickness(0) };
        content.Children.Add(label);
        content.Children.Add(passwordBox);
        content.Children.Add(buttons);

        var dialog = new Window
        {
            Title = request.Operation == HumanCredentialOperation.Login ? "Cashier sign in" : "Fresh authentication",
            Content = content,
            Width = 430,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        if (_owner.IsLoaded)
        {
            dialog.Owner = _owner;
        }

        lock (_sync)
        {
            _activeDialog = dialog;
            _activeAttemptReference = request.AttemptReference;
            _activeHostCorrelationId = request.HostCorrelationId;
        }

        dialog.Loaded += (_, _) =>
        {
            passwordBox.Clear();
            enteredAfterPresentation = false;
            acceptButton.IsEnabled = false;
            passwordBox.Focus();
        };
        passwordBox.PasswordChanged += (_, _) =>
        {
            if (!dialog.IsLoaded || passwordBox.SecurePassword.Length == 0)
            {
                acceptButton.IsEnabled = false;
                return;
            }

            if (!enteredAfterPresentation)
            {
                _trace.Record(
                    "prompt.credential-entered",
                    request.Operation.ToString().ToUpperInvariant(),
                    nameof(WpfHumanCredentialPrompt),
                    "NATIVE_PASSWORD_EDIT",
                    true,
                    request.AttemptReference,
                    request.HostCorrelationId);
            }
            enteredAfterPresentation = true;
            acceptButton.IsEnabled = true;
        };
        acceptButton.Click += (_, _) =>
        {
            if (!enteredAfterPresentation || passwordBox.SecurePassword.Length == 0)
            {
                return;
            }

            submittedCredential = passwordBox.Password;
            accepted = true;
            submitTrigger = "NATIVE_EXPLICIT_SUBMIT";
            _trace.Record(
                "prompt.submitted",
                request.Operation.ToString().ToUpperInvariant(),
                nameof(WpfHumanCredentialPrompt),
                submitTrigger,
                true,
                request.AttemptReference,
                request.HostCorrelationId);
            dialog.DialogResult = true;
        };

        using var cancellationRegistration = cancellationToken.Register(
            () => CancelActive("OPERATION_CANCELLED"));
        try
        {
            dialog.ShowDialog();
            return new HumanCredentialPromptResult(request.AttemptReference, accepted, submittedCredential, submitTrigger);
        }
        finally
        {
            passwordBox.Clear();
            lock (_sync)
            {
                if (_activeAttemptReference == request.AttemptReference)
                {
                    _activeDialog = null;
                    _activeAttemptReference = null;
                    _activeHostCorrelationId = null;
                }
            }
            _trace.Record(
                "prompt.closed",
                request.Operation.ToString().ToUpperInvariant(),
                nameof(WpfHumanCredentialPrompt),
                submitTrigger,
                accepted,
                request.AttemptReference,
                request.HostCorrelationId,
                outcome: accepted ? "ACCEPTED" : "CANCELLED");
        }
    }
}
