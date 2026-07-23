using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class TerminalShellReadinessTests
{
    [Fact]
    public void Selector_UsesCurrentTerminalShellMarker()
    {
        Assert.Equal("apt-terminal-shell", TerminalShellReadiness.DataTestId);
        Assert.Contains("apt-terminal-shell", TerminalShellReadiness.ReadySelectorForScript);
        Assert.DoesNotContain("apt-mode1-shell", TerminalShellReadiness.ReadySelectorForScript);
    }

    [Fact]
    public void BuildMountFailureDetail_ReportsStartupErrorWithoutObsoleteModeWording()
    {
        var detail = TerminalShellReadiness.BuildMountFailureDetail(
            "APT-WV2-20260722091040101",
            "http://127.0.0.1:5179/?receiptVisualSmoke=1",
            "Cannot read properties of undefined");

        Assert.Contains("[data-testid='apt-terminal-shell'][data-app-ready='true']", detail);
        Assert.Contains("Startup error: Cannot read properties of undefined", detail);
        Assert.DoesNotContain("Mode 1", detail);
        Assert.DoesNotContain("React Mode 1", detail);
        Assert.DoesNotContain("apt-mode1-shell", detail);
    }
}
