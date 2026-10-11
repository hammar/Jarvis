using PersonalAgent.Application;
using System.Text;
using Xunit;

namespace PersonalAgent.UnitTests;

public sealed class ConversationTitlePolicyTests
{
    [Theory]
    [InlineData("  Keep   these\nwords\tapart  ", "Keep these words apart")]
    [InlineData("Cafe\u0301 plans", "Café plans")]
    [InlineData("\u001B\u0000\u007F", "Message without displayable text")]
    [InlineData(" \t\n ", "Message without displayable text")]
    [InlineData("\u200B\u2060", "Message without displayable text")]
    [InlineData(" \u200B \u2060 ", "Message without displayable text")]
    [InlineData("👩\u200D💻 plans", "👩\u200D💻 plans")]
    [InlineData("Visible\u2060text", "Visible\u2060text")]
    [InlineData("\uFE0E\uFE0F", "Message without displayable text")]
    [InlineData("\U000E0100", "Message without displayable text")]
    [InlineData("\u0301\u034F", "Message without displayable text")]
    [InlineData("\u115F\u1160\u3164\uFFA0", "Message without displayable text")]
    [InlineData("❤\uFE0F plans", "❤\uFE0F plans")]
    [Trait("Category", "Unit")]
    public void AutomaticTitleNormalizesUnicodeAndCollapsesWhitespace(string input, string expected)
    {
        Assert.Equal(expected, ConversationTitlePolicy.CreateAutomaticTitle(input));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AutomaticTitleReplacesControlCharactersBeforeWhitespaceNormalization()
    {
        Assert.Equal(
            "Original [31mtext",
            ConversationTitlePolicy.CreateAutomaticTitle("Original\u001B[31mtext"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void AutomaticTitleUsesAnEllipsisWithoutSplittingUnicodeScalars()
    {
        var title = ConversationTitlePolicy.CreateAutomaticTitle(new string('x', 78) + "😀tail");

        Assert.Equal(80, title.EnumerateRunes().Count());
        Assert.EndsWith("…", title, StringComparison.Ordinal);
        Assert.Equal(new string('x', 78) + "😀…", title);
    }

    [Theory]
    [InlineData("  Renamed  ", "Renamed")]
    [InlineData(" 👩\u200D💻 plans ", "👩\u200D💻 plans")]
    [InlineData(" Visible\u2060text ", "Visible\u2060text")]
    [InlineData(" ❤\uFE0F plans ", "❤\uFE0F plans")]
    [InlineData(" Cafe\u0301 ", "Cafe\u0301")]
    [Trait("Category", "Unit")]
    public void ManualTitleIsTrimmed(string input, string expected)
    {
        Assert.Equal(expected, ConversationTitlePolicy.ValidateManualTitle(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\ntitle")]
    [InlineData("\u200B\u2060")]
    [InlineData(" \u200B \u2060 ")]
    [InlineData("\uFE0E\uFE0F")]
    [InlineData("\U000E0100")]
    [InlineData("\u0301\u034F")]
    [InlineData("\u115F\u1160\u3164\uFFA0")]
    [InlineData("too long")]
    [Trait("Category", "Unit")]
    public void InvalidManualTitlesAreRejected(string title)
    {
        var value = title == "too long" ? new string('x', 201) : title;
        Assert.Throws<ArgumentException>(() => ConversationTitlePolicy.ValidateManualTitle(value));
    }
}
