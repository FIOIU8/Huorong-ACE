using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace HuorongAce.Core.Configuration;

/// <summary>
/// Runtime settings, persisted as <c>config.json</c> next to the executable.
/// </summary>
/// <remarks>
/// The JSON property names are kept identical to the Go version
/// (<c>log_path</c>, <c>poll_interval_sec</c>, <c>keywords</c>,
/// <c>monitor_enabled</c>) via <see cref="JsonPropertyNameAttribute"/>, so an
/// existing config.json written by the Go build keeps working unchanged.
/// </remarks>
public sealed class AppConfig
{
    /// <summary>Path used by earlier Go builds. It never existed on real Huorong installs.</summary>
    public const string LegacyLogPath = @"C:\ProgramData\Huorong\Sysdiag\log.db";

    private static readonly string[] DefaultKeywords =
    [
        "病毒", "木马", "Trojan", "Virus", "Malware", "风险", "威胁", "Win32", "Backdoor", "Rootkit"
    ];

    [JsonPropertyName("log_path")]
    public string LogPath { get; set; } = QuarantineDatabasePath();

    [JsonPropertyName("poll_interval_sec")]
    public int PollIntervalSeconds { get; set; } = 2;

    [JsonPropertyName("keywords")]
    public List<string> Keywords { get; set; } = new(DefaultKeywords);

    [JsonPropertyName("monitor_enabled")]
    public bool MonitorEnabled { get; set; } = true;

    /// <summary>
    /// Where Huorong records the threats it processed. Every row of its
    /// <c>FilesV3_60</c> table is a quarantined threat.
    /// </summary>
    public static string QuarantineDatabasePath()
    {
        var programData = Environment.GetEnvironmentVariable("ProgramData");
        var root = string.IsNullOrWhiteSpace(programData) ? @"C:\ProgramData" : programData;
        return Path.Combine(root, "Huorong", "Sysdiag", "QuarantineEx.db");
    }

    /// <summary>
    /// Path of config.json. The executable directory is preferred. Protected
    /// install directories fall back to the user's local application data.
    /// </summary>
    public static string ConfigFilePath
    {
        get
        {
            var sidecar = Path.Combine(ExecutableDirectory(), "config.json");
            if (File.Exists(sidecar) || CanWriteDirectory(Path.GetDirectoryName(sidecar)!))
            {
                return sidecar;
            }

            return UserConfigPath();
        }
    }

    public TimeSpan PollInterval =>
        PollIntervalSeconds < 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(PollIntervalSeconds);

    public static AppConfig Load()
    {
        var config = new AppConfig();
        foreach (var path in ConfigPaths())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
                if (loaded is not null)
                {
                    config = loaded;
                }

                break;
            }
            catch (JsonException)
            {
                break;
            }
            catch (IOException)
            {
                // Try the per-user fallback when the sidecar is inaccessible.
            }
        }

        config.MigrateLegacyLogPath();
        return config;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        var sidecar = Path.Combine(ExecutableDirectory(), "config.json");
        try
        {
            WriteAtomically(sidecar, json);
        }
        catch (UnauthorizedAccessException)
        {
            WriteAtomically(UserConfigPath(), json);
        }
        catch (IOException)
        {
            WriteAtomically(UserConfigPath(), json);
        }
    }

    /// <summary>
    /// Reports whether <paramref name="text"/> contains any configured keyword.
    /// </summary>
    /// <remarks>
    /// Only used for custom databases without a virus-name column; the
    /// quarantine database is treated as authoritative (see
    /// <see cref="Monitoring.HuorongQuarantineReader"/>).
    /// </remarks>
    public bool MatchesKeyword(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var lowered = text.ToLowerInvariant();
        foreach (var keyword in Keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword) && lowered.Contains(keyword.ToLowerInvariant()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Repairs configs written by the Go build: their hard-coded log path does
    /// not exist on real Huorong installs, which silently disabled detection.
    /// </summary>
    private void MigrateLegacyLogPath()
    {
        if (!string.Equals(LogPath, LegacyLogPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var quarantine = QuarantineDatabasePath();
        if (!File.Exists(quarantine))
        {
            return;
        }

        LogPath = quarantine;
        try
        {
            Save();
        }
        catch (IOException)
        {
            // Migration is best effort.
        }
    }

    private static IEnumerable<string> ConfigPaths()
    {
        var sidecar = Path.Combine(ExecutableDirectory(), "config.json");
        yield return sidecar;

        var userPath = UserConfigPath();
        if (!string.Equals(sidecar, userPath, StringComparison.OrdinalIgnoreCase))
        {
            yield return userPath;
        }
    }

    private static string ExecutableDirectory()
    {
        var processPath = Environment.ProcessPath;
        var directory = string.IsNullOrWhiteSpace(processPath)
            ? null
            : Path.GetDirectoryName(processPath);

        return string.IsNullOrWhiteSpace(directory)
            ? AppContext.BaseDirectory
            : directory;
    }

    private static string UserConfigPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.GetTempPath();
        }

        return Path.Combine(root, "HuorongACE", "config.json");
    }

    private static bool CanWriteDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".huorong-ace-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void WriteAtomically(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (IOException)
            {
                // Best effort cleanup after a failed replacement.
            }
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
