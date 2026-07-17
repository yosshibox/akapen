namespace AkapenProbe.Ui;

public readonly record struct ImageFolderSelectionResult(string? ImagePath, string? ErrorMessage);

public static class ImageFolderSelection
{
    public static ImageFolderSelectionResult FindFirstSupportedImage(
        string folder,
        IComparer<string> comparer) =>
        FindFirstSupportedImage(folder, Directory.EnumerateFiles, comparer);

    public static ImageFolderSelectionResult FindFirstSupportedImage(
        string folder,
        Func<string, IEnumerable<string>> enumerateFiles,
        IComparer<string> comparer)
    {
        try
        {
            string? first = enumerateFiles(folder)
                .Where(IsSupportedImage)
                .OrderBy(path => path, comparer)
                .FirstOrDefault();
            return new ImageFolderSelectionResult(first, null);
        }
        catch (Exception ex) when (IsExpectedFileSystemFailure(ex))
        {
            return new ImageFolderSelectionResult(null, ex.Message);
        }
    }

    public static bool IsSupportedImage(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp";

    private static bool IsExpectedFileSystemFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
}
