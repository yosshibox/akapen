using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string title, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string target = args.Length == 2 && string.Equals(args[0], "--install-dir", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(args[1])
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Akapen");
            Directory.CreateDirectory(target);
            using Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("AkapenInstaller.Akapen.zip")
                ?? throw new InvalidOperationException("Akapen payload is missing.");
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
            archive.ExtractToDirectory(target, overwriteFiles: true);

            string executable = Path.Combine(target, "Akapen.exe");
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Akapen.lnk"), executable, target);
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Akapen.lnk");
            CreateShortcut(startMenu, executable, target);
            Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = target, UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox(IntPtr.Zero, "Akapenをインストールできませんでした。\n\n" + ex.Message, "Akapen Setup", 0x10);
            return 1;
        }
    }

    private static void CreateShortcut(string shortcutPath, string target, string workingDirectory)
    {
        string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        string command = "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + Escape(shortcutPath) +
            "');$s.TargetPath='" + Escape(target) + "';$s.WorkingDirectory='" + Escape(workingDirectory) + "';$s.Save()";
        using Process process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -Command \"" + command + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not create the Akapen shortcut.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("Could not create the Akapen shortcut.");
    }
}
