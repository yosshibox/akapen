namespace AkapenProbe.Ui;

public static class AppDiagnostics
{
    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Akapen",
        "akapen.log");

    public static void Write(string area, Exception exception) =>
        Write(area, exception.ToString());

    public static void Write(string area, string message)
    {
        try
        {
            string? directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(
                LogPath,
                $"{DateTimeOffset.Now:O} [{area}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never become another application failure.
        }
    }
}
