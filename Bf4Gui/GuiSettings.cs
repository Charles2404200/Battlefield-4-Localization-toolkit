using System.Text.Json;
using Microsoft.Win32;

namespace Bf4Gui;

internal sealed class GuiSettings
{
    public string GameDirectory { get; set; } = "";
    public string CsvPath { get; set; } = "";
    public string FontDirectory { get; set; } = "";
    public string WorkspaceDirectory { get; set; } = "";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BF4VietnameseToolkit", "settings.json");

    public static GuiSettings Load()
    {
        GuiSettings settings;
        try
        {
            settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(SettingsPath)) ?? new()
                : new();
        }
        catch { settings = new(); }
        if (string.IsNullOrWhiteSpace(settings.GameDirectory))
            settings.GameDirectory = FindGame();
        var downloads = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile), "Downloads", "Bf4_VH");
        if (string.IsNullOrWhiteSpace(settings.CsvPath))
            settings.CsvPath = Path.Combine(downloads, "Battlefield4_VietHoa_Unicode.csv");
        if (string.IsNullOrWhiteSpace(settings.FontDirectory))
            settings.FontDirectory = Path.Combine(downloads, "fonts");
        if (string.IsNullOrWhiteSpace(settings.WorkspaceDirectory))
            settings.WorkspaceDirectory = FindWorkspace();
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string FindGame()
    {
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", "Battlefield 4")
        };
        try
        {
            var steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?
                .GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(steam))
                candidates.Add(Path.Combine(steam, "steamapps", "common", "Battlefield 4"));
        }
        catch { }
        return candidates.FirstOrDefault(path => File.Exists(Path.Combine(path, "bf4.exe")))
            ?? candidates[0];
    }

    private static string FindWorkspace()
    {
        for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
            if (Directory.Exists(Path.Combine(at.FullName, "Bf4Toolkit")) &&
                Directory.Exists(Path.Combine(at.FullName, "backups")))
                return at.FullName;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "BF4 Vietnamese Toolkit");
    }
}
