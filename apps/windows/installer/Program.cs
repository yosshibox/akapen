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
            var arguments = new List<string>(args);
            // V1.1.1: unattended installs (deployment scripts) pass
            // --accept-eula to skip the interactive agreement dialog; the
            // license terms still apply.
            bool eulaAccepted = arguments.RemoveAll(a => string.Equals(a, "--accept-eula", StringComparison.OrdinalIgnoreCase)) > 0;
            string target = arguments.Count == 2 && string.Equals(arguments[0], "--install-dir", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(arguments[1])
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Akapen");
            if (!eulaAccepted && !ShowEulaDialog()) return 2;
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

    /// <summary>
    /// Shows the license agreement (embedded EULA-ja.txt) with agree /
    /// cancel buttons. Returns true only when the user explicitly agrees.
    /// </summary>
    private static bool ShowEulaDialog()
    {
        string text;
        using (Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AkapenInstaller.EULA-ja.txt"))
        {
            if (stream == null) throw new InvalidOperationException("使用許諾契約書が見つかりません。");
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }

        System.Windows.Forms.Application.EnableVisualStyles();
        using var form = new System.Windows.Forms.Form
        {
            Text = "Akapen セットアップ - 使用許諾契約書",
            Width = 640,
            Height = 560,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            Font = new System.Drawing.Font("Segoe UI", 9.5f),
        };
        var caption = new System.Windows.Forms.Label
        {
            Text = "インストールを続けるには、以下の使用許諾契約書に同意してください。",
            Dock = System.Windows.Forms.DockStyle.Top,
            Padding = new System.Windows.Forms.Padding(12, 12, 12, 8),
            AutoSize = false,
            Height = 40,
        };
        var body = new System.Windows.Forms.TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = System.Windows.Forms.ScrollBars.Vertical,
            Dock = System.Windows.Forms.DockStyle.Fill,
            Text = text.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"),
            BackColor = System.Drawing.Color.White,
            Font = new System.Drawing.Font("Yu Gothic UI", 9.5f),
        };
        var footer = new System.Windows.Forms.FlowLayoutPanel
        {
            Dock = System.Windows.Forms.DockStyle.Bottom,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            Height = 52,
            Padding = new System.Windows.Forms.Padding(12, 10, 12, 10),
        };
        var cancel = new System.Windows.Forms.Button
        {
            Text = "同意しない",
            DialogResult = System.Windows.Forms.DialogResult.Cancel,
            Width = 110,
            Height = 30,
        };
        var accept = new System.Windows.Forms.Button
        {
            Text = "同意してインストール",
            DialogResult = System.Windows.Forms.DialogResult.OK,
            Width = 160,
            Height = 30,
        };
        footer.Controls.Add(cancel);
        footer.Controls.Add(accept);
        form.Controls.Add(body);
        form.Controls.Add(caption);
        form.Controls.Add(footer);
        form.AcceptButton = accept;
        form.CancelButton = cancel;
        return form.ShowDialog() == System.Windows.Forms.DialogResult.OK;
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
