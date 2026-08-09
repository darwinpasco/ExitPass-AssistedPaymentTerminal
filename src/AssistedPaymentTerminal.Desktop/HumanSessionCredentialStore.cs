using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;

namespace AssistedPaymentTerminal.Desktop;

public interface IHumanSessionCredentialStore
{
    HumanSessionCredential? Load();
    void Save(HumanSessionCredential credential);
    void Delete();
}

public sealed record HumanSessionCredential(Guid SessionReference, string SessionToken);

public sealed class DpapiCurrentUserHumanSessionCredentialStore : IHumanSessionCredentialStore
{
    private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes("ExitPass.APT.HumanSessionCredential.v1"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;

    public DpapiCurrentUserHumanSessionCredentialStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : System.IO.Path.GetFullPath(path);
    }

    public string Path => _path;

    public HumanSessionCredential? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var protectedPayload = File.ReadAllBytes(_path);
            var plaintext = ProtectedData.Unprotect(protectedPayload, Entropy, DataProtectionScope.CurrentUser);
            try
            {
                var credential = JsonSerializer.Deserialize<HumanSessionCredential>(plaintext, JsonOptions);
                return credential is { SessionReference: var reference, SessionToken.Length: > 0 } && reference != Guid.Empty
                    ? credential
                    : null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(HumanSessionCredential credential)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("APT human-session restart protection requires Windows DPAPI CurrentUser.");
        }
        if (credential.SessionReference == Guid.Empty || string.IsNullOrWhiteSpace(credential.SessionToken))
        {
            throw new ArgumentException("A complete APT human-session credential is required.", nameof(credential));
        }

        var directory = System.IO.Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential, JsonOptions);
        try
        {
            var protectedPayload = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            var temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, protectedPayload);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Delete()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            var temporary = _path + ".tmp";
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A stale encrypted credential is never trusted; failed cleanup remains fail closed.
        }
    }

    private static string DefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(localAppData, "ExitPass", "AssistedPaymentTerminal", "HumanSession", "credential.bin");
    }
}
