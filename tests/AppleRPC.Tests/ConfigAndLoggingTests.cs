// SPDX-License-Identifier: AGPL-3.0-only
using AppleRPC.Configuration;
using AppleRPC.Logging;

namespace AppleRPC.Tests;

public class ConfigTests
{
    [Fact]
    public void Loads_json_with_comments_and_trailing_commas()
    {
        var dir = T.TempDir();
        var path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, """
        {
          // the id
          "DiscordApplicationId": "123456789012345678",
          "Language": "en",
          "Artwork": { "ItunesCountry": "IT", },
        }
        """);

        var c = Config.Load(path);

        Assert.Null(c.LoadError);
        Assert.True(c.HasValidApplicationId);
        Assert.Equal("en", c.Language);
        Assert.Equal("IT", c.Artwork.ItunesCountry);
    }

    [Fact]
    public void Missing_file_yields_defaults_and_an_error_message()
    {
        var c = Config.Load(Path.Combine(T.TempDir(), "nope.json"));
        Assert.NotNull(c.LoadError);
        Assert.False(c.HasValidApplicationId);
        Assert.Equal(2, c.StatusDisplayType);
    }

    [Fact]
    public void Broken_json_does_not_throw()
    {
        var path = Path.Combine(T.TempDir(), "appsettings.json");
        File.WriteAllText(path, "{ this is not json");
        var c = Config.Load(path);
        Assert.NotNull(c.LoadError);
        Assert.Equal("it", c.Language);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("YOUR_APP_ID", false)]
    [InlineData("12345", false)]
    [InlineData("123456789012345678", true)]
    [InlineData(" 123456789012345678 ", true)]
    public void Application_id_must_be_a_snowflake(string id, bool valid)
    {
        var c = new Config { DiscordApplicationId = id };
        c.Normalize();
        Assert.Equal(valid, c.HasValidApplicationId);
    }

    [Fact]
    public void Normalize_enforces_discords_15_second_limit_and_clamps_junk()
    {
        var c = new Config { MinUpdateIntervalSeconds = 1, StatusDisplayType = 9, PauseClearAfterSeconds = -50, Language = "fr" };
        c.Artwork.MaxRetries = 99;
        c.Normalize();

        Assert.Equal(15, c.MinUpdateIntervalSeconds);
        Assert.Equal(2, c.StatusDisplayType);
        Assert.Equal(-1, c.PauseClearAfterSeconds);
        Assert.Equal("it", c.Language);
        Assert.Equal(5, c.Artwork.MaxRetries);
    }

    [Fact]
    public void The_shipped_appsettings_file_parses()
    {
        // Walk up from the test binary to the repo and load the real file users will edit.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AppleRPC.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);

        var c = Config.Load(Path.Combine(dir!, "src", "AppleRPC", "appsettings.json"));
        Assert.Null(c.LoadError);
        Assert.Equal(2, c.StatusDisplayType);
        Assert.Equal(15, c.MinUpdateIntervalSeconds);
        Assert.Contains("AppleMusic", c.AppleMusicSourceIds);
    }
}

public class FileLoggerTests
{
    [Fact]
    public void Rotates_when_the_size_limit_is_hit_and_keeps_at_most_maxFiles()
    {
        var dir = T.TempDir();
        var log = new FileLogger(dir, LogLevel.Information, maxBytes: 1024, maxFiles: 3);

        for (var i = 0; i < 200; i++) log.Write(LogLevel.Information, new string('x', 100) + i);

        var files = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "applerpc.1.log", "applerpc.2.log", "applerpc.log" }, files);
        Assert.All(Directory.GetFiles(dir), f => Assert.True(new FileInfo(f).Length <= 1024 + 200));
        Assert.Contains("199", File.ReadAllText(Path.Combine(dir, "applerpc.log")));   // newest lines live in the current file
    }

    [Fact]
    public void Respects_the_minimum_level_and_writes_exceptions()
    {
        var dir = T.TempDir();
        var log = new FileLogger(dir, LogLevel.Warning);

        log.Write(LogLevel.Debug, "hidden");
        log.Write(LogLevel.Error, "visible", new InvalidOperationException("boom"));

        var text = File.ReadAllText(log.CurrentFile);
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("visible", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void Never_throws_even_if_the_folder_is_unusable()
    {
        var file = Path.Combine(T.TempDir(), "iam-a-file");
        File.WriteAllText(file, "x");
        var log = new FileLogger(Path.Combine(file, "sub"));   // a directory below a regular file can't be created
        log.Write(LogLevel.Error, "still fine");
    }
}
