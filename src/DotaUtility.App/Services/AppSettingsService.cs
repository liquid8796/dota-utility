using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotaUtility.App.Services;

public enum CloseBehavior
{
    MinimizeToTray = 0,
    ExitApplication = 1,
}

public sealed record AppSettings(
    CloseBehavior CloseBehavior = CloseBehavior.MinimizeToTray,
    bool RememberCloseChoice = false,
    bool ShowEnemyHeroesOnMinimap = false);

public interface IAppSettingsService
{
    AppSettings Current { get; }

    string SettingsPath { get; }

    void Save(AppSettings settings);
}

public sealed class FileAppSettingsService : IAppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };

    private readonly object _gate = new();
    private AppSettings _current;

    public FileAppSettingsService(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? GetDefaultSettingsPath()
            : settingsPath;
        _current = LoadFromDisk(SettingsPath);
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public string SettingsPath { get; }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            _current = settings;
            try
            {
                var directory = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonSerializer.Serialize(settings, SerializerOptions);
                File.WriteAllText(SettingsPath, json);
            }
            catch (IOException)
            {
                // Keep in-memory settings even if disk write fails transiently.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep in-memory settings even if disk write fails transiently.
            }
        }
    }

    private static string GetDefaultSettingsPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, "DotaUtility", "settings.json");
    }

    private static AppSettings LoadFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions)
                ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}
