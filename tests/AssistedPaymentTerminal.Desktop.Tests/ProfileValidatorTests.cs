using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class ProfileValidatorTests
{
    [Fact]
    public void Validate_AcceptsCashierAssistedTerminal()
    {
        var result = ProfileValidator.Validate("CASHIER_ASSISTED_TERMINAL");

        Assert.True(result.IsValid);
        Assert.Equal("SUPPORTED", result.Code);
    }

    [Fact]
    public void Validate_RefusesMissingProfile()
    {
        var result = ProfileValidator.Validate("");

        Assert.False(result.IsValid);
        Assert.Equal("MISSING_PROFILE", result.Code);
    }

    [Fact]
    public void Validate_RefusesUnsupportedProfile()
    {
        var result = ProfileValidator.Validate("ADMIN_WORKSTATION");

        Assert.False(result.IsValid);
        Assert.Equal("UNSUPPORTED_PROFILE", result.Code);
    }

    [Fact]
    public void Validate_RefusesContinuityTerminal()
    {
        var result = ProfileValidator.Validate("CONTINUITY_TERMINAL");

        Assert.False(result.IsValid);
        Assert.Equal("MODE2_NOT_IMPLEMENTED", result.Code);
        Assert.Contains("Assisted Payment Terminal shell", result.Message);
        Assert.DoesNotContain("Mode 1", result.Message);
    }
}
