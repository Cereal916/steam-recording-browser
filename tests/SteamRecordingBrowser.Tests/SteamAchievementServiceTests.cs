using System.Text;
using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class SteamAchievementServiceTests
{
    [Fact]
    public void ReadNames_UsesRequestedLanguageAndFallsBackToEnglish()
    {
        using var fixture = new AchievementSchemaFixture();

        var german = SteamAchievementService.ReadNames(fixture.Path, "german");
        var unsupported = SteamAchievementService.ReadNames(fixture.Path, "unsupported");

        Assert.Equal("Der Tod kommt von oben", german["PvE04"]);
        Assert.Equal("Death From Above", unsupported["PvE04"]);
    }

    [Fact]
    public void ReadNames_MapsSteamLanguageAliases()
    {
        using var fixture = new AchievementSchemaFixture();

        var names = SteamAchievementService.ReadNames(fixture.Path, "zh-CN");

        Assert.Equal("空中死神", names["PvE04"]);
    }

    private sealed class AchievementSchemaFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SteamAchievementServiceTests",
            Guid.NewGuid().ToString("N"),
            "UserGameStatsSchema_42.bin");

        public AchievementSchemaFixture()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var writer = new BinaryWriter(File.Create(Path), Encoding.UTF8, leaveOpen: false);
            WriteObject(writer, "42", () =>
            {
                WriteObject(writer, "achievement", () =>
                {
                    WriteString(writer, "name", "PvE04");
                    WriteObject(writer, "display", () =>
                    {
                        WriteObject(writer, "name", () =>
                        {
                            WriteString(writer, "english", "Death From Above");
                            WriteString(writer, "german", "Der Tod kommt von oben");
                            WriteString(writer, "schinese", "空中死神");
                            WriteString(writer, "token", "PvE04_Title");
                        });
                    });
                });
            });
        }

        public void Dispose() => Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, recursive: true);

        private static void WriteObject(BinaryWriter writer, string key, Action writeChildren)
        {
            writer.Write((byte)0);
            WriteNullTerminated(writer, key);
            writeChildren();
            writer.Write((byte)8);
        }

        private static void WriteString(BinaryWriter writer, string key, string value)
        {
            writer.Write((byte)1);
            WriteNullTerminated(writer, key);
            WriteNullTerminated(writer, value);
        }

        private static void WriteNullTerminated(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.UTF8.GetBytes(value));
            writer.Write((byte)0);
        }
    }
}
