namespace AkapenProbe.Ui;

public static class ReviewOutputLayout
{
    public const string DefaultFolderName = "_review";
    public const string TransientFolderName = "strokes";

    public static string ResolveSiblingReviewDirectory(string sourceImagePath, string configuredFolderName)
    {
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(sourceImagePath))
            ?? throw new ArgumentException("The source image must have a parent directory.", nameof(sourceImagePath));
        string sourceFolderName = Path.GetFileName(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string folderName = string.IsNullOrWhiteSpace(configuredFolderName) ||
            string.Equals(configuredFolderName, DefaultFolderName, StringComparison.OrdinalIgnoreCase)
            ? sourceFolderName + DefaultFolderName
            : configuredFolderName;
        return Path.Combine(sourceDirectory, folderName);
    }

    public static string ResolveTransientDirectory(string outputDirectory) =>
        Path.Combine(outputDirectory, TransientFolderName);

    public static void DeleteTrackedArtifacts(IEnumerable<string> artifactPaths, IEnumerable<string> directories)
    {
        foreach (string path in artifactPaths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (string directory in directories)
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static bool IsArtifactForStem(string filePath, string stem, string suffix, string extension)
    {
        string name = Path.GetFileName(filePath);
        string exact = $"{stem}.{suffix}{extension}";
        string collisionPrefix = $"{stem}-";
        return string.Equals(name, exact, StringComparison.OrdinalIgnoreCase) ||
            (name.StartsWith(collisionPrefix, StringComparison.OrdinalIgnoreCase) &&
             name.EndsWith($".{suffix}{extension}", StringComparison.OrdinalIgnoreCase));
    }

    public static string ResolveCollisionFreeFlatPath(string outputDirectory, string fileName)
    {
        string candidate = Path.Combine(outputDirectory, fileName);
        if (!File.Exists(candidate)) return candidate;

        string extension = Path.GetExtension(fileName);
        string withoutExtension = Path.GetFileNameWithoutExtension(fileName);
        int suffixSeparator = withoutExtension.LastIndexOf('.');
        string stem = suffixSeparator > 0 ? withoutExtension[..suffixSeparator] : withoutExtension;
        string artifactSuffix = suffixSeparator > 0 ? withoutExtension[suffixSeparator..] + extension : extension;
        for (int index = 1; ; index++)
        {
            candidate = Path.Combine(outputDirectory, $"{stem}-{index:000}{artifactSuffix}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
