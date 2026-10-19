// SPDX-License-Identifier: AGPL-3.0-only
namespace AppleRPC.Configuration;

/// <summary>Well-known folders. Everything the app writes lives under %LOCALAPPDATA%\AppleRPC.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleRPC");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");
    public static string ArtworkCacheFile => Path.Combine(DataDirectory, "artwork-cache.json");

    /// <summary>appsettings.json sits next to the executable.</summary>
    public static string ConfigFile => Path.Combine(AppContext.BaseDirectory, "appsettings.json");
}
