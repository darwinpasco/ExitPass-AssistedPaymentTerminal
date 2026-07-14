using System.IO;

namespace AssistedPaymentTerminal.Desktop;

public static class WebViewSourceResolver
{
    public static Uri Resolve(StartupOptions options)
    {
        if (!options.PreferPackagedAssets && !string.IsNullOrWhiteSpace(options.DevelopmentWebUiUrl))
        {
            return ResolveDevelopmentUri(options.DevelopmentWebUiUrl);
        }

        return ResolvePackagedAssetUri(options.BaseDirectory);
    }

    public static Uri ResolveDevelopmentUri(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("APT_WEB_UI_URL must be an absolute HTTP or HTTPS URL.");
        }

        return uri;
    }

    public static Uri ResolvePackagedAssetUri(string baseDirectory)
    {
        var indexPath = Path.Combine(baseDirectory, "wwwroot", "index.html");
        if (!File.Exists(indexPath))
        {
            throw new FileNotFoundException(
                "Packaged frontend assets were not found. Run npm run app:build before production-style desktop smoke.",
                indexPath);
        }

        return new Uri(indexPath);
    }
}
