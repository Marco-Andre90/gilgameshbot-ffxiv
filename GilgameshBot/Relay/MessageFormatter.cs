using System.Text;

namespace GilgameshBot.Relay;

/// <summary>Text helpers for turning game chat into Discord message content.</summary>
public static class MessageFormatter
{
    /// <summary>Discord's hard limit for message content.</summary>
    public const int MaxLength = 2000;

    private const char ZeroWidthSpace = '\u200B';

    /// <summary>
    /// Produces <c>**Sender**: body</c>, where <paramref name="body"/> is already escaped and
    /// has its mentions inserted. Truncates to Discord's limit on a safe boundary.
    /// </summary>
    public static string Compose(string senderName, string body)
    {
        var content = $"**{EscapeMarkdown(senderName)}**: {body}";
        if (content.Length <= MaxLength)
            return content;

        var cut = MaxLength - 1;
        if (char.IsHighSurrogate(content[cut - 1]))
            cut--;

        return content[..cut] + "…";
    }

    /// <summary>
    /// Escapes Discord markdown so players cannot format the Discord message or hide links behind
    /// <c>[text](url)</c>. Mention tokens are inserted <em>after</em> escaping (see <see cref="MentionResolver"/>).
    /// </summary>
    public static string EscapeMarkdown(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\':
                case '*':
                case '_':
                case '~':
                case '`':
                case '|':
                case '>':
                case '[':
                case ']':
                    sb.Append('\\');
                    break;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Prevents "@everyone" / "@here" typed in game from ever being a real mass mention.
    /// The explicit <c>AllowedMentions</c> on every send already forbids them; this keeps the text honest too.
    /// </summary>
    public static string NeutraliseMassMentions(string text) =>
        text.Replace("@everyone", "@" + ZeroWidthSpace + "everyone", StringComparison.OrdinalIgnoreCase)
            .Replace("@here", "@" + ZeroWidthSpace + "here", StringComparison.OrdinalIgnoreCase);
}
