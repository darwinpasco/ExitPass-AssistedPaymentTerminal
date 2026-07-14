using AssistedPaymentTerminal.Desktop;
using Xunit;

namespace AssistedPaymentTerminal.Desktop.Tests;

public sealed class WebViewSourceResolverTests
{
    [Fact]
    public void ResolveDevelopmentUri_AcceptsHttpUrl()
    {
        var uri = WebViewSourceResolver.ResolveDevelopmentUri("http://localhost:5173");

        Assert.Equal("http://localhost:5173/", uri.ToString());
    }

    [Fact]
    public void Resolve_UsesDevelopmentUrlWhenConfigured()
    {
        var options = new StartupOptions("CASHIER_ASSISTED_TERMINAL", "http://localhost:5173", AppContext.BaseDirectory, false, false);

        var uri = WebViewSourceResolver.Resolve(options);

        Assert.Equal("http://localhost:5173/", uri.ToString());
    }

    [Fact]
    public void ResolvePackagedAssetUri_UsesWwwrootIndex()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"apt-desktop-test-{Guid.NewGuid():N}");
        var wwwroot = Path.Combine(temp, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<html></html>");

        try
        {
            var uri = WebViewSourceResolver.ResolvePackagedAssetUri(temp);

            Assert.Equal(new Uri(Path.Combine(wwwroot, "index.html")), uri);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void ResolvePackagedAssetUri_FailsWhenIndexMissing()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"apt-desktop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);

        try
        {
            var exception = Assert.Throws<FileNotFoundException>(() => WebViewSourceResolver.ResolvePackagedAssetUri(temp));

            Assert.Contains("Packaged frontend assets were not found", exception.Message);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
