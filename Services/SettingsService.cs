using System.IO;
using Newtonsoft.Json;

namespace Recliner.Services;

public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Recliner");

    private static readonly string SettingsFile =
        Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                string json = File.ReadAllText(SettingsFile);
                return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }

        // Return defaults; try to auto-detect the distro
        var settings = new AppSettings();
        var distros = WslService.GetDistros();
        if (distros.Count > 0)
            settings.WslDistro = distros[0];
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
        }
        catch { }
    }
}
