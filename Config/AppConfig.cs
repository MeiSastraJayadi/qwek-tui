using System.Text.Json;

namespace Qwek.Config;

public sealed record AppConfig(string SendKey)
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    public bool SendOnCtrlEnter =>
        string.Equals(SendKey, "ctrl+enter", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(SendKey, "ctrl-enter", StringComparison.OrdinalIgnoreCase);

    public static string ConfigDirectory { get; } = ResolveConfigDirectory();

    public static string ConfigPath =>
        Path.Combine(ConfigDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new AppConfig("enter");
            }

            string payload = File.ReadAllText(ConfigPath);
            AppConfig? config = JsonSerializer.Deserialize<AppConfig>(payload, Json);

            if (config is null || string.IsNullOrWhiteSpace(config.SendKey))
            {
                return new AppConfig("enter");
            }

            string sendKey = config.SendKey.Trim();

            return sendKey.Equals("ctrl+enter", StringComparison.OrdinalIgnoreCase) ||
                   sendKey.Equals("ctrl-enter", StringComparison.OrdinalIgnoreCase)
                ? new AppConfig("ctrl+enter")
                : new AppConfig("enter");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppConfig("enter");
        }
    }

    private static string ResolveConfigDirectory()
    {
        string? overrideDirectory =
            Environment.GetEnvironmentVariable("QWEK_CONFIG_DIR");

        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory);
        }

        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

            return Path.Combine(appData, "Qwek");
        }

        string? xdgConfig =
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string configDirectory = string.IsNullOrWhiteSpace(xdgConfig)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config")
            : xdgConfig;

        return Path.Combine(configDirectory, "qwek");
    }
}
