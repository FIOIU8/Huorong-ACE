using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>Path of config.json, alongside the executable.</summary>
    public static string ConfigFilePath
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            return Path.Combine(dir, "config.json");
        }
    }

    public TimeSpan PollInterval =>
        PollIntervalSeconds < 1 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(PollIntervalSeconds);

    public static AppConfig Load()
    {
        var config = new AppConfig();
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
                if (loaded is not null)
                {
                    config = loaded;
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt file must not prevent start-up; defaults are used.
        }

        config.MigrateLegacyLogPath();
        return config;
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        File.WriteAllText(ConfigFilePath, json);
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

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
