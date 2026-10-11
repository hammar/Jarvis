using System.Globalization;
using System.Text;

namespace PersonalAgent.Application;

/// <summary>Defines deterministic title rules for owner-visible conversations.</summary>
public static class ConversationTitlePolicy
{
    /// <summary>Maximum number of Unicode scalar values in an automatically derived title.</summary>
    public const int MaximumAutomaticTitleScalars = 80;

    /// <summary>Normalizes and shortens the first accepted user message for display as a title.</summary>
    /// <param name="message">Untrusted user message text.</param>
    /// <returns>A nonempty, whitespace-normalized title of at most 80 Unicode scalar values.
    /// Uses "Message without displayable text" when no visible base rune remains.
    /// Preserves format characters accompanying visible text, including emoji joiners.</returns>
    public static string CreateAutomaticTitle(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var displayText = new StringBuilder(message.Length);
        foreach (var rune in message.EnumerateRunes())
        {
            displayText.Append(Rune.IsControl(rune) ? ' ' : rune.ToString());
        }

        string normalized;
        try
        {
            normalized = displayText.ToString().Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            normalized = displayText.ToString();
        }
        var title = string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var scalars = title.EnumerateRunes().ToArray();
        if (!ContainsDisplayableText(title))
        {
            return "Message without displayable text";
        }

        if (scalars.Length <= MaximumAutomaticTitleScalars)
        {
            return title;
        }

        return string.Concat(scalars.Take(MaximumAutomaticTitleScalars - 1).Select(rune => rune.ToString())) + "…";
    }

    /// <summary>Trims and validates a manually selected conversation title.</summary>
    /// <param name="title">Owner-provided title text.</param>
    /// <returns>The trimmed title suitable for durable storage.</returns>
    /// <exception cref="ArgumentException">The title has no displayable text, is too long,
    /// or contains control characters.</exception>
    public static string ValidateManualTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var normalized = title.Trim();
        if (normalized.Length is 0 or > 200 || normalized.Any(char.IsControl) ||
            !ContainsDisplayableText(normalized))
        {
            throw new ArgumentException("Conversation title must contain displayable text within 1 through 200 non-control characters.", nameof(title));
        }

        return normalized;
    }

    // Default-ignorable Hangul fillers are letters, unlike format characters and marks.
    private static bool ContainsDisplayableText(string title) =>
        title.EnumerateRunes().Any(rune => !Rune.IsWhiteSpace(rune) &&
            rune.Value is not (0x115F or 0x1160 or 0x3164 or 0xFFA0) &&
            Rune.GetUnicodeCategory(rune) is not (
                UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or
                UnicodeCategory.EnclosingMark or UnicodeCategory.OtherNotAssigned));
}
