using Velopack;

namespace ScTelemetryGateway.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        if (Environment.GetEnvironmentVariable("SCTG_INSTALLER_TEST") == "1" &&
            InstallerSmoke.TryRun(args, out var result))
            return result;
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\StarCitizenTelemetryGateway", out var first);
        if (!first)
        {
            MessageBox.Show("Star Citizen Telemetry Gateway is already running in this session.",
                TrayContext.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        try
        {
            using var context = new TrayContext(args.Contains("--smoke-test", StringComparer.Ordinal) ||
                Environment.GetEnvironmentVariable("SCTG_INSTALLER_TEST") == "1",
                installationUsageChoice: InstallationUsageChoice.Parse(args));
            Application.Run(context);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            MessageBox.Show("Cannot load settings or decrypt the API key. No collection was started.\n\n" +
                "Check the files in %LOCALAPPDATA%\\StarCitizenTelemetryGateway.\n\n" + ex.Message,
                TrayContext.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
