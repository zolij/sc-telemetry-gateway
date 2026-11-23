namespace ScTelemetryGateway.Windows;

internal static class InstallationUsageChoice
{
    private const string Prefix = "--installation-usage-statistics=";

    public static bool? Parse(string[] args)
    {
        bool? choice = null;
        foreach (var arg in args)
        {
            if (!arg.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (choice.HasValue) throw new ArgumentException("Duplicate installation usage choice.");
            choice = arg[Prefix.Length..] switch
            {
                "enabled" => true,
                "disabled" => false,
                _ => throw new ArgumentException("Invalid installation usage choice.")
            };
        }
        return choice;
    }
}
