using System.Text.Json;
using SteamRecordingBrowser.Models;
using SteamRecordingBrowser.Services;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class MetadataServiceTests : IDisposable
{
    private const string Session = "bg_480_20260102_120000";
    private const string LegacyKey = "bg:480:20260102:120000";
    private const string FirstClip = "clip_480_20260102_123000";
    private const string SecondClip = "clip_480_20260102_124500";
    private readonly string _metadataRoot = Path.Combine(
        Path.GetTempPath(), "SteamRecordingBrowserMetadataTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("1808500", false, "steam://open/screenshots/1808500")]
    [InlineData("1808500", true, "steam://open/recording/1808500")]
    public void GetRecordingUri_UsesTheMatchingSteamMediaDestination(
        string appId,
        bool isAutoRecording,
        string expected)
    {
        var uri = SteamService.GetRecordingUri(appId, isAutoRecording);

        Assert.Equal(expected, uri.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-app")]
    [InlineData("0")]
    public void GetRecordingUri_RejectsInvalidSteamAppIds(string appId)
    {
        Assert.Throws<ArgumentException>(() => SteamService.GetRecordingUri(appId, false));
    }

    [Fact]
    public void GetRecordingKey_UsesStableSteamBackgroundIdentity()
    {
        var path = @"C:\recordings\bg_1808500_20260819_025416\session.mpd";

        var key = MetadataService.GetRecordingKey(path);

        Assert.Equal("bg:1808500:20260819:025416", key);
    }

    [Theory]
    [InlineData(Session)]
    [InlineData("bg_480_20260102_120000_0")]
    [InlineData("fg_480_20260102_120000")]
    public void GetRecordingKey_DistinguishesSavedClipsFromTheSameSession(string session)
    {
        Assert.Equal("clip:480:20260102:123000", MetadataService.GetRecordingKey(ClipPath(FirstClip, session)));
        Assert.Equal("clip:480:20260102:124500", MetadataService.GetRecordingKey(ClipPath(SecondClip, session)));
    }

    [Fact]
    public void GetRecordingKey_PreservesClipSuffixAndSurvivesMovingAnOfflineLibrary()
    {
        var original = ClipPath(FirstClip + "_1");
        var moved = original.Replace(@"C:\recordings", @"D:\moved-library").ToUpperInvariant();

        Assert.Equal("clip:480:20260102:123000:1", MetadataService.GetRecordingKey(original));
        Assert.Equal(MetadataService.GetRecordingKey(original), MetadataService.GetRecordingKey(moved));
        Assert.NotEqual(MetadataService.GetRecordingKey(ClipPath(FirstClip)), MetadataService.GetRecordingKey(original));
    }

    [Fact]
    public void GetRecordingKey_UsesFullPathForUnrecognizedRecordings()
    {
        Assert.Equal(@"path:c:\other\video\session.mpd", MetadataService.GetRecordingKey(@"C:\Other\video\session.mpd"));
    }

    [Fact]
    public void SavedClips_KeepEditsAndDeletionsIndependentAfterReload()
    {
        var metadata = CreateService();
        var first = CreateItem(ClipPath(FirstClip));
        var second = CreateItem(ClipPath(SecondClip));
        metadata.ApplyTo(first);
        metadata.ApplyTo(second);

        Assert.NotSame(metadata.ForRecording(first.Path), metadata.ForRecording(second.Path));
        first.Tags = ["First tag"];
        first.Description = "First description";
        first.IsFavorite = true;
        metadata.UpdateFrom(first);
        second.Tags = ["Second tag"];
        second.Description = "Second description";
        second.IsFavorite = true;
        metadata.UpdateFrom(second);

        metadata.Load();
        metadata.ApplyTo(first);
        metadata.ApplyTo(second);
        Assert.Equal(new[] { "First tag" }, first.Tags);
        Assert.Equal(new[] { "Second tag" }, second.Tags);
        Assert.Equal("First description", first.Description);
        Assert.Equal("Second description", second.Description);
        Assert.True(first.IsFavorite);
        Assert.True(second.IsFavorite);

        first.Tags = ["Edited tag"];
        metadata.UpdateFrom(first);
        metadata.Load();
        metadata.ApplyTo(first);
        metadata.ApplyTo(second);
        Assert.Equal(new[] { "Edited tag" }, first.Tags);
        Assert.Equal(new[] { "Second tag" }, second.Tags);

        first.Tags = [];
        first.Description = "";
        first.IsFavorite = false;
        metadata.UpdateFrom(first);
        metadata.Load();
        metadata.ApplyTo(first);
        metadata.ApplyTo(second);

        Assert.Empty(first.Tags);
        Assert.Empty(first.Description);
        Assert.False(first.IsFavorite);
        Assert.Equal(new[] { "Second tag" }, second.Tags);
        Assert.Equal("Second description", second.Description);
        Assert.True(second.IsFavorite);
        using var saved = JsonDocument.Parse(File.ReadAllText(metadata.LibraryPath));
        Assert.Single(saved.RootElement.GetProperty("Entries").EnumerateArray());
    }

    [Theory]
    [InlineData(LegacyKey)]
    [InlineData("path:old-library-location")]
    [InlineData("")]
    public void Load_MigratesLegacyClipMetadataOnlyToItsRecordedOwnerAndBacksUpTheOriginal(string legacyKey)
    {
        var metadata = CreateService();
        var owner = CreateItem(ClipPath(SecondClip));
        var sibling = CreateItem(ClipPath(FirstClip));
        WriteLegacyStore(new MetadataEntry
        {
            RecordingKey = legacyKey,
            Path = owner.Path,
            Favorite = true,
            Description = "Preserved description",
            Tags = ["Preserved tag"]
        });
        var original = File.ReadAllText(metadata.LibraryPath);

        metadata.Load();
        // Scanning a sibling or the original background recording first must
        // not claim the legacy annotations or change the migration's owner.
        metadata.ApplyTo(sibling);
        var background = metadata.ForRecording($@"C:\recordings\{Session}\session.mpd");
        metadata.ApplyTo(owner);

        Assert.Empty(sibling.Tags);
        Assert.Empty(sibling.Description);
        Assert.False(sibling.IsFavorite);
        Assert.Empty(background.Tags);
        Assert.Empty(background.Description);
        Assert.False(background.Favorite);
        Assert.Equal(new[] { "Preserved tag" }, owner.Tags);
        Assert.Equal("Preserved description", owner.Description);
        Assert.True(owner.IsFavorite);
        var backup = Assert.Single(Directory.GetFiles(_metadataRoot, "library_before_clip_identity_*.json"));
        Assert.Equal(original, File.ReadAllText(backup));

        using var saved = JsonDocument.Parse(File.ReadAllText(metadata.LibraryPath));
        Assert.Equal(3, saved.RootElement.GetProperty("SchemaVersion").GetInt32());
        var row = Assert.Single(saved.RootElement.GetProperty("Entries").EnumerateArray());
        Assert.Equal("clip:480:20260102:124500", row.GetProperty("RecordingKey").GetString());

        var migratedJson = File.ReadAllText(metadata.LibraryPath);
        metadata.Load();
        Assert.Equal(migratedJson, File.ReadAllText(metadata.LibraryPath));
        Assert.Equal(backup, Assert.Single(Directory.GetFiles(_metadataRoot, "library_before_clip_identity_*.json")));
        metadata.ApplyTo(owner);
        Assert.Equal(new[] { "Preserved tag" }, owner.Tags);
    }

    [Fact]
    public void Load_MigratesLegacyEntryArraysWithoutRequiringRecordingFiles()
    {
        var metadata = CreateService();
        var ownerPath = ClipPath(FirstClip);
        File.WriteAllText(metadata.LibraryPath, JsonSerializer.Serialize(new[]
        {
            new MetadataEntry { Path = ownerPath, Tags = ["Old tag"] }
        }));

        metadata.Load();

        Assert.Equal(new[] { "Old tag" }, metadata.ForRecording(ownerPath).Tags);
        Assert.Empty(metadata.ForRecording(ClipPath(SecondClip)).Tags);
        Assert.Single(Directory.GetFiles(_metadataRoot, "library_before_clip_identity_*.json"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Load_PrefersExplicitClipMetadataOverLegacyEntriesRegardlessOfOrder(bool legacyFirst, bool cleared)
    {
        var metadata = CreateService();
        var ownerPath = ClipPath(FirstClip);
        var legacy = new MetadataEntry { RecordingKey = LegacyKey, Path = ownerPath, Tags = ["Stale tag"] };
        var current = new MetadataEntry
        {
            RecordingKey = MetadataService.GetRecordingKey(ownerPath),
            Path = ownerPath.Replace(@"C:\recordings", @"D:\moved-library"),
            Tags = cleared ? [] : ["Current tag"]
        };
        WriteLegacyStore(legacyFirst ? [legacy, current] : [current, legacy]);

        metadata.Load();
        var owner = metadata.ForRecording(ownerPath);

        Assert.Equal(current.Tags, owner.Tags);
        Assert.Equal(current.Tags, metadata.GetKnownTags());
        Assert.Empty(metadata.ForRecording(ClipPath(SecondClip)).Tags);
        metadata.Load();
        Assert.Equal(current.Tags, metadata.ForRecording(ownerPath).Tags);
    }

    [Fact]
    public void Load_LeavesBackgroundMetadataAndPathFallbacksCompatible()
    {
        var metadata = CreateService();
        var backgroundPath = $@"C:\recordings\{Session}\session.mpd";
        var unknownPath = @"C:\recordings\unrecognized\session.mpd";
        WriteLegacyStore(
            new MetadataEntry { RecordingKey = LegacyKey, Path = backgroundPath, Tags = ["Background tag"] },
            new MetadataEntry { Path = unknownPath, Tags = ["Fallback tag"] });
        var original = File.ReadAllText(metadata.LibraryPath);

        metadata.Load();

        Assert.Equal(new[] { "Background tag" }, metadata.ForRecording(backgroundPath).Tags);
        Assert.Equal(new[] { "Fallback tag" }, metadata.ForRecording(unknownPath).Tags);
        Assert.Empty(metadata.ForRecording(ClipPath(FirstClip)).Tags);
        Assert.Equal(original, File.ReadAllText(metadata.LibraryPath));
        Assert.Empty(Directory.GetFiles(_metadataRoot, "library_before_clip_identity_*.json"));
    }

    [Fact]
    public void Load_DoesNotAssignAnExplicitClipEntryToAnotherClipThroughAStalePath()
    {
        var metadata = CreateService();
        WriteLegacyStore(new MetadataEntry
        {
            RecordingKey = MetadataService.GetRecordingKey(ClipPath(FirstClip)),
            Path = ClipPath(SecondClip),
            Tags = ["Owner tag"]
        });

        metadata.Load();

        Assert.Empty(metadata.ForRecording(ClipPath(SecondClip)).Tags);
        Assert.Equal(new[] { "Owner tag" }, metadata.ForRecording(ClipPath(FirstClip)).Tags);
    }

    [Fact]
    public void Import_MigratesLegacyClipMetadataAndKeepsTheSourceAndSafetyBackup()
    {
        var metadata = CreateService();
        var owner = CreateItem(ClipPath(SecondClip));
        var sibling = CreateItem(ClipPath(FirstClip));
        sibling.Tags = ["Before import"];
        metadata.UpdateFrom(sibling);
        var source = Path.Combine(_metadataRoot, "legacy-import.json");
        var importedJson = JsonSerializer.Serialize(new
        {
            SchemaVersion = 2,
            Entries = new[] { new MetadataEntry { RecordingKey = LegacyKey, Path = owner.Path, Tags = ["Imported tag"] } }
        });
        File.WriteAllText(source, importedJson);

        var result = metadata.Import(source, [sibling, owner]);

        Assert.Equal(1, result.Matched);
        Assert.Equal(1, result.Tagged);
        Assert.Empty(sibling.Tags);
        Assert.Equal(new[] { "Imported tag" }, owner.Tags);
        Assert.Equal(importedJson, File.ReadAllText(source));
        using var backup = JsonDocument.Parse(File.ReadAllText(result.SafetyBackup));
        var previous = Assert.Single(backup.RootElement.GetProperty("Entries").EnumerateArray());
        Assert.Equal("Before import", previous.GetProperty("Tags")[0].GetString());
        metadata.Load();
        Assert.Empty(metadata.ForRecording(sibling.Path).Tags);
        Assert.Equal(new[] { "Imported tag" }, metadata.ForRecording(owner.Path).Tags);
    }

    [Fact]
    public void GetKnownTags_UsesAllStoredRecordingsAndReflectsEdits()
    {
        var metadata = CreateService();
        var first = metadata.ForRecording(@"C:\tag-test\first\session.mpd");
        var second = metadata.ForRecording(@"C:\tag-test\second\session.mpd");
        first.Tags = new List<string> { "Boss", " Funny " };
        second.Tags = new List<string> { "BOSS", "Action" };

        Assert.Equal(new[] { "Action", "Boss", "Funny" }, metadata.GetKnownTags());

        second.Tags = new List<string> { "New tag" };
        Assert.Equal(new[] { "Boss", "Funny", "New tag" }, metadata.GetKnownTags());
    }

    [Fact]
    public void NormalizeTags_SplitsDeduplicatesSortsAndTrims()
    {
        var tags = new[]
        {
            "boss, funny",
            "Funny",
            "  action  "
        };

        var normalized = MetadataService.NormalizeTags(tags);

        Assert.Equal(new[] { "action", "boss", "funny" }, normalized);
    }

    private MetadataService CreateService() => new(_metadataRoot);

    private void WriteLegacyStore(params MetadataEntry[] entries) =>
        File.WriteAllText(Path.Combine(_metadataRoot, "library.json"),
            JsonSerializer.Serialize(new { SchemaVersion = 2, Entries = entries }));

    private static string ClipPath(string clip, string session = Session) =>
        $@"C:\recordings\clips\{clip}\video\{session}\session.mpd";

    private static RecordingItem CreateItem(string path) => new()
    {
        Path = path,
        Folder = Session,
        GameId = "480",
        GameName = "Test game",
        Timestamp = DateTime.UnixEpoch,
        SizeBytes = 0,
        DurationSeconds = 1
    };

    public void Dispose()
    {
        if (Directory.Exists(_metadataRoot))
            Directory.Delete(_metadataRoot, recursive: true);
    }
}
