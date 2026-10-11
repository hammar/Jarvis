using System.Text;

namespace PersonalAgent.Application;

/// <summary>Defines deterministic title rules for owner-visible conversations.</summary>
public static class ConversationTitlePolicy
{
    /// <summary>Maximum number of Unicode scalar values in an automatically derived title.</summary>
    public const int MaximumAutomaticTitleScalars = 80;

    /// <summary>Normalizes and shortens the first accepted user message for display as a title.</summary>
    /// <param name="message">Untrusted user message text.</param>
    /// <returns>A whitespace-normalized title of at most 80 Unicode scalar values.</returns>
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
        if (scalars.Length <= MaximumAutomaticTitleScalars)
        {
            return title;
        }

        return string.Concat(scalars.Take(MaximumAutomaticTitleScalars - 1).Select(rune => rune.ToString())) + "…";
    }

    /// <summary>Trims and validates a manually selected conversation title.</summary>
    /// <param name="title">Owner-provided title text.</param>
    /// <returns>The trimmed title suitable for durable storage.</returns>
    /// <exception cref="ArgumentException">The title is empty, too long, or contains control characters.</exception>
    public static string ValidateManualTitle(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        var normalized = title.Trim();
        if (normalized.Length is 0 or > 200 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Conversation title must contain 1 through 200 non-control characters.", nameof(title));
        }

        return normalized;
    }
}
