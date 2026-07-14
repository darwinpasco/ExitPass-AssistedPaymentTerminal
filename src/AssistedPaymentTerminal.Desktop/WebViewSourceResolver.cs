using System.IO;

namespace AssistedPaymentTerminal.Desktop;

public static class WebViewSourceResolver
{
    public static WebViewSource Resolve(StartupOptions options)
    {
        if (!options.PreferPackagedAssets && !string.IsNullOrWhiteSpace(options.DevelopmentWebUiUrl))
        {
            return ResolveDevelopmentSource(options.DevelopmentWebUiUrl);
        }

        return ResolvePackagedAssetSource(options.BaseDirectory);
    }

    public static Uri ResolveDevelopmentUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("APT_WEB_UI_URL must be an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    public static WebViewSource ResolveDevelopmentSource(string url)
    {
        var uri = ResolveDevelopmentUri(url);

        return new WebViewSource(
            NavigationUri: uri,
            IsPackaged: false,
            PackagedAssetsDirectory: null,
            VirtualHostName: null,
            SafeDisplayLocation: uri.GetLeftPart(UriPartial.Authority));
    }

    public static WebViewSource ResolvePackagedAssetSource(string baseDirectory)
    {
        var wwwroot = Path.Combine(baseDirectory, "wwwroot");
        var indexPath = Path.Combine(wwwroot, "index.html");
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException(
                "Packaged frontend assets were not found. Run npm run app:build before production-style desktop smoke.",
                indexPath);
        }

        return new WebViewSource(
            NavigationUri: new Uri($"https://{WebViewSource.PackagedHostName}/index.html"),
            IsPackaged: true,
            PackagedAssetsDirectory: wwwroot,
            VirtualHostName: WebViewSource.PackagedHostName,
            SafeDisplayLocation: $"packaged frontend assets via https://{WebViewSource.PackagedHostName}/index.html");
    }
}
