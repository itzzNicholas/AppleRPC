// SPDX-License-Identifier: AGPL-3.0-only
using System.Text.Json;
using System.Text.RegularExpressions;
using AppleRPC.Logging;

namespace AppleRPC.Configuration;

/// <summary>User settings, loaded from appsettings.json. No secret lives here: the Discord Application ID is public.</summary>
public sealed class Config
{
    public const string GitHubUrl = "https://github.com/itzzNicholas/AppleRPC";
    public const string ButtonLabel = "⭐ Star on GitHub";

    public string DiscordApplicationId { get; set; } = "";
    public string Language { get; set; } = "it";
    public int StatusDisplayType { get; set; } = 2;
    public int PauseClearAfterSeconds { get; set; } = 120;
    public int MinUpdateIntervalSeconds { get; set; } = 15;
    public bool ShowGitHubButton { get; set; } = true;
    public string DefaultAsset { get; set; } = "applerpc";
    public List<string> AppleMusicSourceIds { get; set; } = new() { "AppleMusic", "AppleInc.AppleMusicWin" };
    public ArtworkConfig Artwork { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    /// <summary>Why the file couldn't be used (null when it loaded fine). Not serialized.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadError { get; private set; }

    /// <summary>Discord application IDs are snowflakes: 17-20 digits.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasValidApplicationId => Regex.IsMatch(DiscordApplicationId ?? "", @"^\d{17,20}$");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads and sanitizes the config. Never throws: on any problem it returns defaults and sets <see cref="LoadError"/>.</summary>
    public static Config Load(string path)
    {
        Config config;
        string? error = null;
        try
        {
            if (!File.Exists(path))
            {
                config = new Config();
                error = $"Config file not found: {path}";
            }
            else
            {
                config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions) ?? new Config();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            config = new Config();
            error = $"Could not read {path}: {ex.Message}";
        }

        config.Normalize();
        config.LoadError = error;
        return config;
    }

    /// <summary>Clamps values to what Discord / the app can actually handle.</summary>
    public void Normalize()
    {
        DiscordApplicationId = (DiscordApplicationId ?? "").Trim();
        Language = string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "it";
        if (StatusDisplayType is < 0 or > 2) StatusDisplayType = 2;
        // Discord drops presence updates sent more often than once every 15 s.
        MinUpdateIntervalSeconds = Math.Max(15, MinUpdateIntervalSeconds);
        PauseClearAfterSeconds = Math.Max(-1, PauseClearAfterSeconds);
        if (string.IsNullOrWhiteSpace(DefaultAsset)) DefaultAsset = "applerpc";
        AppleMusicSourceIds = (AppleMusicSourceIds ?? new()).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (AppleMusicSourceIds.Count == 0) AppleMusicSourceIds = new() { "AppleMusic", "AppleInc.AppleMusicWin" };

        Artwork ??= new();
        Artwork.RequestTimeoutSeconds = Math.Clamp(Artwork.RequestTimeoutSeconds, 1, 60);
        Artwork.MaxRetries = Math.Clamp(Artwork.MaxRetries, 0, 5);
        Artwork.LookupBudgetSeconds = Math.Clamp(Artwork.LookupBudgetSeconds, 1, 60);
        Artwork.CacheDays = Math.Clamp(Artwork.CacheDays, 1, 365);
        if (string.IsNullOrWhiteSpace(Artwork.ItunesCountry)) Artwork.ItunesCountry = "US";

        Logging ??= new();
        Logging.MaxFileSizeKB = Math.Clamp(Logging.MaxFileSizeKB, 64, 102400);
        Logging.MaxFiles = Math.Clamp(Logging.MaxFiles, 1, 20);
    }

    public FileLogger CreateLogger(string? directory = null) => new(
        directory ?? AppPaths.LogDirectory,
        Enum.TryParse<LogLevel>(Logging.Level, ignoreCase: true, out var level) ? level : LogLevel.Information,
        Logging.MaxFileSizeKB * 1024L,
        Logging.MaxFiles);
}

public sealed class ArtworkConfig
{
    public bool Enabled { get; set; } = true;
    public string ItunesCountry { get; set; } = "US";
    public bool UseArtistPhoto { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 6;
    public int MaxRetries { get; set; } = 2;
    public int LookupBudgetSeconds { get; set; } = 8;
    public int CacheDays { get; set; } = 30;
}

public sealed class LoggingConfig
{
    public string Level { get; set; } = "Information";
    public int MaxFileSizeKB { get; set; } = 1024;
    public int MaxFiles { get; set; } = 3;
}
