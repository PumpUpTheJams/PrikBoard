using System.Text.RegularExpressions;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// A Slack message's text as a card shows it. Slack writes mentions, channels and links between angle brackets
/// (<c>&lt;@U123&gt;</c>, <c>&lt;#C123|general&gt;</c>, <c>&lt;https://x|label&gt;</c>) and escapes <c>&amp;</c>,
/// <c>&lt;</c> and <c>&gt;</c>; this turns them into names, Markdown links and plain characters, like Discord's clean content.
/// </summary>
public static partial class SlackText
{
    [GeneratedRegex(@"<([^<>]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex Special();

    /// <summary>The text with mentions as <c>@name</c> (<paramref name="userName"/> looks people up; null keeps the id).</summary>
    public static string Plain(string text, Func<string, string?> userName) =>
        Unescape(Special().Replace(text, match =>
        {
            var inner = match.Groups[1].Value;
            var bar = inner.IndexOf('|');
            var (target, label) = bar < 0 ? (inner, null) : (inner[..bar], inner[(bar + 1)..]);
            if (target.Length == 0) return match.Value;
            return target[0] switch
            {
                '@' => "@" + (label ?? userName(target[1..]) ?? target[1..]),
                '#' => "#" + (label ?? target[1..]),
                // Special mentions such as <!here> or <!subteam^S123|@team>.
                '!' => label ?? "@" + target[1..].Split('^')[0],
                _ when label is not null && label != target => $"[{label}]({target})",
                _ => target,
            };
        }));

    private static string Unescape(string text) => text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
}
