using FinTrack.Budget.Domain.Journal;

namespace FinTrack.Budget.Domain.Tests.Journal;

/// <summary>
/// The descriptive fields every entry has (ARCHITECTURE 4.2): note, tags and source. Input is checked
/// before it reaches the domain (validators), so invalid values here are bugs and throw.
/// </summary>
public class EntryInfoTests
{
    [Fact]
    public void None_HasNoNoteNoTags_AndIsManual()
    {
        EntryInfo.None.Note.ShouldBeNull();
        EntryInfo.None.Tags.ShouldBeEmpty();
        EntryInfo.None.Source.ShouldBe(EntrySource.Manual);
    }

    [Theory]
    [InlineData("Salary")]
    [InlineData("Two lines\nof note")]
    [InlineData("Windows\r\nline break")]
    public void ValidNotes(string note) => EntryInfo.IsValidNote(note).ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(" Salary")]
    [InlineData("Salary\n")]
    [InlineData("Tab\there")]
    public void InvalidNotes(string note) => EntryInfo.IsValidNote(note).ShouldBeFalse();

    [Fact]
    public void Notes_HaveAtMost500Characters()
    {
        EntryInfo.IsValidNote(new string('a', EntryInfo.MaxNoteLength)).ShouldBeTrue();
        EntryInfo.IsValidNote(new string('a', EntryInfo.MaxNoteLength + 1)).ShouldBeFalse();
        EntryInfo.MaxNoteLength.ShouldBe(500);
    }

    [Theory]
    [InlineData("car repair")]
    [InlineData("2026")]
    [InlineData("exactly-thirty-characters-long")]
    public void ValidTags(string tag) => EntryInfo.IsValidTag(tag).ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData(" car")]
    [InlineData("car ")]
    [InlineData("car\nrepair")]
    [InlineData("a-tag-that-is-much-longer-than-thirty")]
    public void InvalidTags(string tag) => EntryInfo.IsValidTag(tag).ShouldBeFalse();

    [Fact]
    public void Tags_AreAtMostTen_AndDistinctWithoutRegardToCase()
    {
        EntryInfo.AreValidTags([.. Enumerable.Range(1, 10).Select(number => $"t{number}")]).ShouldBeTrue();
        EntryInfo.AreValidTags([.. Enumerable.Range(1, 11).Select(number => $"t{number}")]).ShouldBeFalse();
        EntryInfo.AreValidTags(["Car", "car"]).ShouldBeFalse();
    }

    [Fact]
    public void Tags_WithOneInvalidAmongValidOnes_AreInvalid() =>
        EntryInfo.AreValidTags(["work", "family", " padded"]).ShouldBeFalse();

    [Fact]
    public void MissingSource_IsABug() =>
        Should.Throw<ArgumentException>(() => new EntryInfo(null, [], default));

    [Fact]
    public void InvalidValues_AreBugs()
    {
        Should.Throw<ArgumentException>(() => new EntryInfo(" note", [], EntrySource.Manual));
        Should.Throw<ArgumentException>(() => new EntryInfo(null, ["a", "A"], EntrySource.Manual));
    }

    [Fact]
    public void Tags_AreCopied()
    {
        var tags = new List<string> { "a" };
        var info = new EntryInfo(null, tags, EntrySource.Manual);

        tags.Add("b");

        info.Tags.ShouldBe(["a"]);
    }

    [Theory]
    [InlineData("Manual")]
    [InlineData("Import:binance")]
    [InlineData("Agent:insights")]
    [InlineData("Import:my_bank-2")]
    public void Source_ReadsItsStoredForm(string text)
    {
        EntrySource.TryParse(text, out var source).ShouldBeTrue();
        source.ToString().ShouldBe(text);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("Import:")]
    [InlineData("Import:Binance")]
    [InlineData("Agent: insights")]
    [InlineData("Robot:x")]
    [InlineData("")]
    public void Source_RejectsAnythingElse(string text) =>
        EntrySource.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Source_Factories()
    {
        EntrySource.Import("binance").ToString().ShouldBe("Import:binance");
        EntrySource.Agent("insights").ToString().ShouldBe("Agent:insights");
        Should.Throw<ArgumentException>(() => EntrySource.Import("Bad Name"));
    }
}
