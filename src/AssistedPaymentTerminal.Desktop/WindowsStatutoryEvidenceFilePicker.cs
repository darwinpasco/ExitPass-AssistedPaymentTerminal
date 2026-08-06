using System.IO;
using Microsoft.Win32;

namespace AssistedPaymentTerminal.Desktop;

public interface IStatutoryEvidenceFilePicker
{
    StatutoryEvidenceFileCandidate? SelectSingleImage();
}

public sealed class WindowsStatutoryEvidenceFilePicker : IStatutoryEvidenceFilePicker
{
    public StatutoryEvidenceFileCandidate? SelectSingleImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select statutory evidence image",
            Filter = "JPEG or PNG image (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            CheckFileExists = true,
            Multiselect = false,
            AddExtension = false,
            DereferenceLinks = true
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        var path = Path.GetFullPath(dialog.FileName);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            _ => string.Empty
        };
        var file = new FileInfo(path);
        return new StatutoryEvidenceFileCandidate(
            path,
            SanitizeDisplayName(file.Name),
            contentType,
            file.Exists ? file.Length : -1);
    }

    internal static string SanitizeDisplayName(string fileName)
    {
        var safeName = Path.GetFileName(fileName).Trim();
        if (safeName.Length > 80)
        {
            safeName = $"{Path.GetFileNameWithoutExtension(safeName)[..60]}{Path.GetExtension(safeName)}";
        }

        return string.IsNullOrWhiteSpace(safeName) ? "Selected image" : safeName;
    }
}

public sealed record StatutoryEvidenceFileCandidate(
    string Path,
    string DisplayName,
    string ContentType,
    long Length);
