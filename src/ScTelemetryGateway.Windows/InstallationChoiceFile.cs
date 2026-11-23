namespace ScTelemetryGateway.Windows;

internal static class InstallationChoiceFile
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StarCitizenTelemetryGateway", "installation-usage-choice");

    public static bool? Read(string path)
    {
        string value;
        try { value = File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        return value switch
        {
            "enabled" => true,
            "disabled" => false,
            _ => throw new InvalidDataException("The installation usage choice is invalid.")
        };
    }

    public static void Write(string path, bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The installation choice path must have a parent directory."));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, enabled ? "enabled" : "disabled");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
