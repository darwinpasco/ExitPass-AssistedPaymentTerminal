using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class TerminalHardwareStartupValidationTests
{
    private const string ExpectedBios = "PF6BJ1WA";
    private const string ExpectedSsd = "ACE4_2E00_653A_D952";
    private const string DiskUniqueId = "eui.ACE42E00653AD952";

    [Fact]
    public void Validate_AcceptsMatchingBiosAndSelectedSsd()
    {
        var guard = CreateGuard(
            ExpectedBios,
            new WindowsPhysicalDiskIdentity(DiskUniqueId, "SKHynix_HFS001TEM9X169N", ExpectedSsd + "."));

        var result = guard.Validate();

        Assert.Equal(ExpectedBios, result.BiosSerial);
        Assert.Equal(ExpectedSsd, result.SsdSerial);
        Assert.Equal(DiskUniqueId, result.PhysicalDiskUniqueId);
    }

    [Fact]
    public void Validate_RejectsBiosMismatchWhenSsdMatches()
    {
        var guard = CreateGuard(
            "OTHER-BIOS",
            new WindowsPhysicalDiskIdentity(DiskUniqueId, "NVMe", ExpectedSsd));

        var error = Assert.Throws<TerminalHardwareStartupException>(guard.Validate);

        Assert.Equal("APT_BIOS_SERIAL_MISMATCH", error.Code);
    }

    [Fact]
    public void Validate_RejectsSsdMismatchWhenBiosMatches()
    {
        var guard = CreateGuard(
            ExpectedBios,
            new WindowsPhysicalDiskIdentity(DiskUniqueId, "NVMe", "OTHER-SSD"));

        var error = Assert.Throws<TerminalHardwareStartupException>(guard.Validate);

        Assert.Equal("APT_SSD_SERIAL_MISMATCH", error.Code);
    }

    [Theory]
    [InlineData(null, ExpectedSsd, DiskUniqueId)]
    [InlineData(ExpectedBios, null, DiskUniqueId)]
    [InlineData(ExpectedBios, ExpectedSsd, null)]
    [InlineData("BAD\nBIOS", ExpectedSsd, DiskUniqueId)]
    [InlineData(ExpectedBios, "BAD\rSSD", DiskUniqueId)]
    public void Validate_RejectsMissingOrMalformedExpectations(
        string? bios,
        string? ssd,
        string? diskUniqueId)
    {
        var guard = new TerminalHardwareStartupGuard(
            new TerminalHardwareStartupExpectations(bios, ssd, diskUniqueId),
            new FixedBiosReader(ExpectedBios),
            new FixedDiskReader(new WindowsPhysicalDiskIdentity(DiskUniqueId, "NVMe", ExpectedSsd)));

        var error = Assert.Throws<TerminalHardwareStartupException>(guard.Validate);

        Assert.Equal("APT_HARDWARE_EXPECTATION_INVALID", error.Code);
    }

    [Fact]
    public void Validate_RejectsMissingSelectedDisk()
    {
        var guard = CreateGuard(
            ExpectedBios,
            new WindowsPhysicalDiskIdentity("eui.UNRELATED", "Other NVMe", ExpectedSsd));

        var error = Assert.Throws<TerminalHardwareStartupException>(guard.Validate);

        Assert.Equal("APT_PHYSICAL_DISK_NOT_FOUND", error.Code);
    }

    [Fact]
    public void Validate_RejectsBlankSelectedDiskSerial()
    {
        var guard = CreateGuard(
            ExpectedBios,
            new WindowsPhysicalDiskIdentity(DiskUniqueId, "NVMe", "   ."));

        var error = Assert.Throws<TerminalHardwareStartupException>(guard.Validate);

        Assert.Equal("APT_SSD_SERIAL_UNREADABLE", error.Code);
    }

    [Fact]
    public void Validate_RejectsAmbiguousSelectedDisk()
    {
        var guard = CreateGuard(
            ExpectedBios,
            new WindowsPhysicalDiskIdentity(DiskUniqueId, "NVMe A", ExpectedSsd),
            new WindowsPhysicalDiskIdentity(DiskUniqueId.ToUpperInvariant(), "NVMe B", ExpectedSsd));

        var error = Assert.Throws<TerminalHardwareStartupException>(guard.Validate);

        Assert.Equal("APT_PHYSICAL_DISK_AMBIGUOUS", error.Code);
    }

    [Fact]
    public void Validate_SelectsByUniqueIdWhenEnumerationOrderChanges()
    {
        var selected = new WindowsPhysicalDiskIdentity(DiskUniqueId, "Selected NVMe", ExpectedSsd + ".");
        var other = new WindowsPhysicalDiskIdentity("eui.UNRELATED", "Other NVMe", "UNRELATED");
        var first = CreateGuard(ExpectedBios, selected, other).Validate();
        var second = CreateGuard(ExpectedBios, other, selected).Validate();

        Assert.Equal(ExpectedSsd, first.SsdSerial);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Validate_PropagatesBoundedHardwareQueryTimeout()
    {
        var guard = new TerminalHardwareStartupGuard(
            Expectations(),
            new FixedBiosReader(ExpectedBios),
            new ThrowingDiskReader(new TerminalHardwareStartupException(
                "APT_PHYSICAL_DISK_QUERY_TIMEOUT",
                "The physical SSD identity query timed out.")));

        var error = Assert.Throws<TerminalHardwareStartupException>(guard.Validate);

        Assert.Equal("APT_PHYSICAL_DISK_QUERY_TIMEOUT", error.Code);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Preflight_RefusesBeforeSourceResolution_ForNormalAndSmokeRoutes(
        bool smokeCheckOnly,
        bool webViewSmokeCheck)
    {
        var resolverCalled = false;
        var options = Options(smokeCheckOnly, webViewSmokeCheck);
        var guard = new ThrowingGuard();

        var error = Assert.Throws<TerminalHardwareStartupException>(() =>
            DesktopStartupPreflight.ValidateHardwareAndResolveSource(
                options,
                guard,
                _ =>
                {
                    resolverCalled = true;
                    return WebViewSourceResolver.ResolveDevelopmentSource("http://127.0.0.1:5373");
                }));

        Assert.Equal("APT_BIOS_SERIAL_MISMATCH", error.Code);
        Assert.False(resolverCalled);
    }

    [Fact]
    public void Preflight_AllowsOperationalSourceAfterHardwarePasses()
    {
        var resolverCalled = false;
        var source = DesktopStartupPreflight.ValidateHardwareAndResolveSource(
            Options(smokeCheckOnly: false, webViewSmokeCheck: false),
            new FixedGuard(),
            _ =>
            {
                resolverCalled = true;
                return WebViewSourceResolver.ResolveDevelopmentSource("http://127.0.0.1:5373");
            });

        Assert.True(resolverCalled);
        Assert.Equal("http://127.0.0.1:5373/", source.NavigationUri.AbsoluteUri);
    }

    [Fact]
    public void PhysicalDiskJsonParser_RejectsUnreadableInventory()
    {
        var error = Assert.Throws<TerminalHardwareStartupException>(() =>
            WindowsPowerShellPhysicalDiskIdentityReader.Parse("not-json"));

        Assert.Equal("APT_PHYSICAL_DISK_QUERY_INVALID", error.Code);
    }

    private static TerminalHardwareStartupGuard CreateGuard(
        string actualBios,
        params WindowsPhysicalDiskIdentity[] disks) =>
        new(Expectations(), new FixedBiosReader(actualBios), new FixedDiskReader(disks));

    private static TerminalHardwareStartupExpectations Expectations() =>
        new(ExpectedBios, ExpectedSsd, DiskUniqueId);

    private static StartupOptions Options(bool smokeCheckOnly, bool webViewSmokeCheck) =>
        new(
            ProfileValidator.CashierAssistedTerminal,
            "http://127.0.0.1:5373",
            AppContext.BaseDirectory,
            PreferPackagedAssets: false,
            smokeCheckOnly,
            webViewSmokeCheck);

    private sealed class FixedBiosReader(string value) : IGovernedPhysicalTerminalSerialReader
    {
        public string Read() => value;
    }

    private sealed class FixedDiskReader(params WindowsPhysicalDiskIdentity[] disks) : IWindowsPhysicalDiskIdentityReader
    {
        public IReadOnlyList<WindowsPhysicalDiskIdentity> Read() => disks;
    }

    private sealed class ThrowingDiskReader(Exception exception) : IWindowsPhysicalDiskIdentityReader
    {
        public IReadOnlyList<WindowsPhysicalDiskIdentity> Read() => throw exception;
    }

    private sealed class ThrowingGuard : ITerminalHardwareStartupGuard
    {
        public TerminalHardwareStartupEvidence Validate() =>
            throw new TerminalHardwareStartupException(
                "APT_BIOS_SERIAL_MISMATCH",
                "Physical BIOS serial mismatch.");
    }

    private sealed class FixedGuard : ITerminalHardwareStartupGuard
    {
        public TerminalHardwareStartupEvidence Validate() =>
            new(ExpectedBios, DiskUniqueId, "SKHynix_HFS001TEM9X169N", ExpectedSsd);
    }
}
