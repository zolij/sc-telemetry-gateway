using System.Diagnostics;
using System.Reflection;

namespace ScTelemetryGateway.Installer;

internal static class Program
{
    private const string ProductName = "Star Citizen Telemetry Gateway";

    [STAThread]
    private static int Main(string[] args)
    {
        if (Environment.GetEnvironmentVariable("SCTG_INSTALLER_TEST") == "1" &&
            args is ["--privacy-ui-smoke-test"])
            return PrivacyDialog.SmokeTest();

        var silent = args.Length == 1 && args[0] == "--silent";
        if (args.Length != 0 && !silent)
        {
            MessageBox.Show("Unsupported installer arguments.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        ApplicationConfiguration.Initialize();
        if (!silent)
        {
            using var dialog = new PrivacyDialog(choice => Task.Run(() => Install(choice)));
            return dialog.ShowDialog() == DialogResult.OK ? 0 : 1;
        }

        try { Install(null); return 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Install(bool? usageEnabled)
    {
        if (Mutex.TryOpenExisting(@"Local\StarCitizenTelemetryGateway", out var running))
        {
            running.Dispose();
            throw new InvalidOperationException("Exit the running application before installing.");
        }

        string? installer = null;
        string? directory = null;
        try
        {
            directory = Path.Combine(Path.GetTempPath(), "sctg-setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            installer = Path.Combine(directory, "VelopackSetup.exe");
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("VelopackSetup.exe")
                ?? throw new InvalidDataException("The installer payload is missing."))
            using (var file = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                payload.CopyTo(file);

            using var process = Process.Start(new ProcessStartInfo(installer, "--silent") { UseShellExecute = false })
                ?? throw new InvalidOperationException("The application installer could not be started.");
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Installation failed (exit code {process.ExitCode}).");

            if (usageEnabled.HasValue)
            {
                var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Spaceling.StarCitizenTelemetryGateway", "current", "StarCitizenTelemetryGateway.exe");
                var choice = usageEnabled.Value ? "enabled" : "disabled";
                using var application = Process.Start(new ProcessStartInfo(executable, $"--installation-usage-statistics={choice}")
                {
                    UseShellExecute = false
                }) ?? throw new InvalidOperationException("The installed application could not be started.");
            }
        }
        finally
        {
            if (installer is not null && File.Exists(installer)) File.Delete(installer);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory);
        }
    }
}
