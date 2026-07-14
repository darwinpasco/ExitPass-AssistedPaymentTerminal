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
        var options = new StartupOptions("CASHIER_ASSISTED_TERMINAL", "http://localhost:5173", AppContext.BaseDirectory, false, false, false);

        var source = WebViewSourceResolver.Resolve(options);

        Assert.Equal("http://localhost:5173/", source.NavigationUri.ToString());
        Assert.False(source.IsPackaged);
        Assert.Null(source.PackagedAssetsDirectory);
        Assert.Null(source.VirtualHostName);
        Assert.Equal("http://localhost:5173", source.SafeDisplayLocation);
    }

    [Fact]
    public void ResolvePackagedAssetSource_MapsWwwrootToStableVirtualHost()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"apt-desktop-test-{Guid.NewGuid():N}");
        var wwwroot = Path.Combine(temp, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        File.WriteAllText(Path.Combine(wwwroot, "index.html"), "<html></html>");

        try
        {
            var source = WebViewSourceResolver.ResolvePackagedAssetSource(temp);

            Assert.Equal("https://apt.local/index.html", source.NavigationUri.ToString());
            Assert.True(source.IsPackaged);
            Assert.Equal(wwwroot, source.PackagedAssetsDirectory);
            Assert.Equal(WebViewSource.PackagedHostName, source.VirtualHostName);
            Assert.Contains("packaged frontend assets", source.SafeDisplayLocation);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void ResolvePackagedAssetSource_FailsWhenIndexMissing()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"apt-desktop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);

        try
        {
            var exception = Assert.Throws<FileNotFoundException>(() => WebViewSourceResolver.ResolvePackagedAssetSource(temp));

            Assert.Contains("Packaged frontend assets were not found", exception.Message);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
