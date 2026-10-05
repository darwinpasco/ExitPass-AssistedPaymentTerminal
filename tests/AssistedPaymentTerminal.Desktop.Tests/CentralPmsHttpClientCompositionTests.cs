using System.Text.RegularExpressions;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsHttpClientCompositionTests
{
    [Fact]
    public void CentralPmsClients_UseManualClientCertificateSelectionWithoutServerValidationBypass()
    {
        var source = File.ReadAllText(FindMainWindowSource());

        Assert.Equal(
            2,
            Regex.Matches(
                source,
                @"ClientCertificateOptions\s*=\s*ClientCertificateOption\.Manual")
                .Count);
        Assert.DoesNotContain("ClientCertificateOption.Automatic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerCertificateCustomValidationCallback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DangerousAcceptAnyServerCertificateValidator", source, StringComparison.Ordinal);
    }

    private static string FindMainWindowSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "AssistedPaymentTerminal.Desktop",
                "MainWindow.xaml.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("MainWindow.xaml.cs was not found from the test output directory.");
    }
}
