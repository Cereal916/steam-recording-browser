using SteamRecordingBrowser.Models;
using Xunit;

namespace SteamRecordingBrowser.Tests;

public sealed class TagEditorModelTests
{
    [Fact]
    public void Suggestions_IgnoreCaseAndWhitespaceAndRankPrefixesBeforeSubstringMatches()
    {
        var editor = new TagEditorModel([], ["Final boss", "boss fight", "Boss", "Funny", "BOSS"]);

        Assert.Equal(new[] { "Boss", "boss fight", "Final boss" }, editor.GetSuggestions("  bOs  "));
        Assert.Empty(editor.GetSuggestions("unknown"));
    }

    [Fact]
    public void Suggestions_ExcludeSelectedTagsAndRestoreRemovedTags()
    {
        var editor = new TagEditorModel(["BOSS"], ["Boss", "boss fight"]);

        Assert.Equal(new[] { "boss fight" }, editor.GetSuggestions("bos"));
        Assert.True(editor.RemoveTag("bOsS"));
        Assert.Equal(new[] { "Boss", "boss fight" }, editor.GetSuggestions("bos"));
    }

    [Fact]
    public void AddTags_TrimsSplitsDeduplicatesAndReusesExistingSpelling()
    {
        var editor = new TagEditorModel(["Favorite"], ["Boss Fight", "Favorite"]);

        var added = editor.AddTags(" boss fight, New tag, FAVORITE, BOSS FIGHT, , ");

        Assert.Equal(2, added);
        Assert.Equal(new[] { "Favorite", "Boss Fight", "New tag" }, editor.Tags);
        Assert.Equal(0, editor.AddTags("  ,  "));
        Assert.Equal(0, editor.AddTags("new TAG"));
    }

    [Fact]
    public void Edits_LeaveOriginalTagsUntouchedUntilCallerSaves()
    {
        var original = new List<string> { "Original" };
        var editor = new TagEditorModel(original, []);

        editor.RemoveTag("Original");
        editor.AddTags("Replacement");

        Assert.Equal(new[] { "Original" }, original);
        Assert.Equal(new[] { "Replacement" }, editor.Tags);
    }

    [Fact]
    public void EmptyQuery_BrowsesAllAvailableTagsIncludingTagsRemovedDuringEditing()
    {
        var editor = new TagEditorModel(["Local tag"], ["Zebra", "Alpha"]);
        editor.AddTags("New tag");
        editor.RemoveTag("New tag");
        editor.RemoveTag("Local tag");

        Assert.Equal(new[] { "Alpha", "Local tag", "New tag", "Zebra" }, editor.GetSuggestions(""));
    }
}
