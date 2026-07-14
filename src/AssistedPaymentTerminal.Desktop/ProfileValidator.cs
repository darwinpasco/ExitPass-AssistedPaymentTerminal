namespace AssistedPaymentTerminal.Desktop;

public sealed record ProfileValidationResult(bool IsValid, string Code, string Message);

public static class ProfileValidator
{
    public const string CashierAssistedTerminal = "CASHIER_ASSISTED_TERMINAL";
    public const string ContinuityTerminal = "CONTINUITY_TERMINAL";

    public static ProfileValidationResult Validate(string? profile)
    {
        var normalized = profile?.Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new ProfileValidationResult(
                false,
                "MISSING_PROFILE",
                "APT_PROFILE is required. Configure CASHIER_ASSISTED_TERMINAL to start this terminal.");
        }

        if (normalized == CashierAssistedTerminal)
        {
            return new ProfileValidationResult(true, "SUPPORTED", "CASHIER_ASSISTED_TERMINAL is supported.");
        }

        if (normalized == ContinuityTerminal)
        {
            return new ProfileValidationResult(
                false,
                "MODE2_NOT_IMPLEMENTED",
                "CONTINUITY_TERMINAL is not implemented in this Mode 1 terminal shell.");
        }

        return new ProfileValidationResult(
            false,
            "UNSUPPORTED_PROFILE",
            $"Unsupported APT_PROFILE '{normalized}'. This shell only supports CASHIER_ASSISTED_TERMINAL.");
    }
}
