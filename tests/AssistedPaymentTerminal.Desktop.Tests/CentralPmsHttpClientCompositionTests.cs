using System.Text.RegularExpressions;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class CentralPmsHttpClientCompositionTests
{
    [Fact]
    public void CentralPmsClients_UseGovernedCertificateWithoutServerValidationBypass()
    {
        var mainWindow = File.ReadAllText(FindSource("MainWindow.xaml.cs"));
        var clientFactory = File.ReadAllText(FindSource("GovernedCentralPmsHttpClientFactory.cs"));
        var source = mainWindow + clientFactory;

        Assert.Equal(
            8,
            Regex.Matches(
                mainWindow,
                @"centralPmsClients\.Create\(")
                .Count);
        Assert.DoesNotContain("new HttpClient(", mainWindow, StringComparison.Ordinal);
        Assert.Contains("ClientCertificateOption.Manual", clientFactory, StringComparison.Ordinal);
        Assert.Contains("handler.ClientCertificates.Add(certificate)", clientFactory, StringComparison.Ordinal);
        Assert.DoesNotContain("ClientCertificateOption.Automatic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerCertificateCustomValidationCallback", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DangerousAcceptAnyServerCertificateValidator", source, StringComparison.Ordinal);
    }

    private static string FindSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "AssistedPaymentTerminal.Desktop",
                fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{fileName} was not found from the test output directory.");
    }
}
