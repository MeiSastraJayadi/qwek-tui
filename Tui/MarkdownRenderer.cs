using System.Text;
using Spectre.Console;

namespace RealTimeAppsTui.Tui;

internal sealed record MarkdownLine(string Markup, int PlainLength);

internal static class MarkdownRenderer
{
    private const string InlineCodeHex = "#e0af68";
    private const string CodeBlockHex = "#7dcfff";
    private const string CodeBorderHex = "#565f89";
    private const string LinkHex = "#7aa2f7";

    private sealed record InlineToken(string Text, string Style);

    private readonly record struct StyledChar(char Character, string Style);

    public static List<MarkdownLine> Render(
        string text,
        int width,
        Func<string, string>? plainFormatter = null)
    {
        width = Math.Max(1, width);
        List<MarkdownLine> lines = [];

        string normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace("\t", "    ");

        bool inCode = false;

        foreach (string rawLine in normalized.Split('\n'))
        {
            if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                AddCodeLines(lines, rawLine, width);
            }
            else
            {
                AddInlineLines(lines, rawLine, width, plainFormatter);
            }
        }

        return lines;
    }

    private static void AddCodeLines(
        List<MarkdownLine> lines,
        string rawLine,
        int width)
    {
        int codeWidth = Math.Max(1, width - 2);

        if (rawLine.Length == 0)
        {
            lines.Add(new MarkdownLine(
                $"[{CodeBorderHex}]│[/] ",
                2));
            return;
        }

        for (int start = 0; start < rawLine.Length; start += codeWidth)
        {
            string chunk = rawLine.Substring(
                start,
                Math.Min(codeWidth, rawLine.Length - start));

            lines.Add(new MarkdownLine(
                $"[{CodeBorderHex}]│[/] [{CodeBlockHex}]{Markup.Escape(chunk)}[/]",
                2 + chunk.Length));
        }
    }

    private static void AddInlineLines(
        List<MarkdownLine> lines,
        string rawLine,
        int width,
        Func<string, string>? plainFormatter)
    {
        List<InlineToken> tokens = Tokenize(rawLine, "");

        if (tokens.Count == 0)
        {
            lines.Add(new MarkdownLine("", 0));
            return;
        }

        List<StyledChar> characters = [];

        foreach (InlineToken token in tokens)
        {
            foreach (char character in token.Text)
            {
                characters.Add(new StyledChar(character, token.Style));
            }
        }

        int cursor = 0;

        while (cursor < characters.Count)
        {
            int remaining = characters.Count - cursor;

            if (remaining <= width)
            {
                lines.Add(BuildLine(
                    characters,
                    cursor,
                    characters.Count,
                    plainFormatter));
                break;
            }

            int limit = cursor + width;
            int split = -1;

            for (int i = limit; i > cursor; i--)
            {
                if (characters[i - 1].Character == ' ')
                {
                    split = i - 1;
                    break;
                }
            }

            int end = split > cursor ? split : limit;
            int trimmedEnd = end;

            while (trimmedEnd > cursor &&
                   characters[trimmedEnd - 1].Character == ' ')
            {
                trimmedEnd--;
            }

            lines.Add(BuildLine(
                characters,
                cursor,
                trimmedEnd,
                plainFormatter));

            cursor = split > cursor ? split + 1 : limit;

            while (cursor < characters.Count &&
                   characters[cursor].Character == ' ')
            {
                cursor++;
            }
        }
    }

    private static MarkdownLine BuildLine(
        List<StyledChar> characters,
        int start,
        int end,
        Func<string, string>? plainFormatter)
    {
        StringBuilder builder = new();
        int index = start;

        while (index < end)
        {
            int next = index + 1;

            while (next < end &&
                   string.Equals(
                       characters[next].Style,
                       characters[index].Style,
                       StringComparison.Ordinal))
            {
                next++;
            }

            string text = new(
                characters
                    .Skip(index)
                    .Take(next - index)
                    .Select(character => character.Character)
                    .ToArray());

            if (characters[index].Style.Length == 0)
            {
                builder.Append(plainFormatter is null
                    ? Markup.Escape(text)
                    : plainFormatter(text));
            }
            else
            {
                builder
                    .Append('[')
                    .Append(characters[index].Style)
                    .Append(']')
                    .Append(Markup.Escape(text))
                    .Append("[/]");
            }

            index = next;
        }

        return new MarkdownLine(builder.ToString(), end - start);
    }

    private static List<InlineToken> Tokenize(string text, string baseStyle)
    {
        List<InlineToken> tokens = [];
        StringBuilder plain = new();

        void FlushPlain()
        {
            if (plain.Length == 0)
            {
                return;
            }

            tokens.Add(new InlineToken(plain.ToString(), baseStyle));
            plain.Clear();
        }

        int index = 0;

        while (index < text.Length)
        {
            char character = text[index];

            if (character == '`')
            {
                int close = text.IndexOf('`', index + 1);

                if (close > index + 1)
                {
                    FlushPlain();
                    tokens.Add(new InlineToken(
                        text[(index + 1)..close],
                        CombineStyles(baseStyle, InlineCodeHex)));
                    index = close + 1;
                    continue;
                }
            }

            if (character is '*' or '_' &&
                index + 1 < text.Length &&
                text[index + 1] == character)
            {
                string marker = new(character, 2);
                int close = text.IndexOf(
                    marker,
                    index + 2,
                    StringComparison.Ordinal);

                if (close > index + 2)
                {
                    FlushPlain();
                    tokens.AddRange(Tokenize(
                        text[(index + 2)..close],
                        CombineStyles(baseStyle, "bold")));
                    index = close + 2;
                    continue;
                }
            }

            if (character is '*' or '_')
            {
                bool canOpen = index + 1 < text.Length &&
                    !char.IsWhiteSpace(text[index + 1]) &&
                    (character == '*' ||
                     index == 0 ||
                     !char.IsLetterOrDigit(text[index - 1]));

                if (canOpen)
                {
                    int close = FindItalicClose(text, index + 1, character);

                    if (close > index + 1)
                    {
                        FlushPlain();
                        tokens.AddRange(Tokenize(
                            text[(index + 1)..close],
                            CombineStyles(baseStyle, "italic")));
                        index = close + 1;
                        continue;
                    }
                }
            }

            if (character == '[')
            {
                int labelEnd = text.IndexOf(']', index + 1);

                if (labelEnd > index + 1 &&
                    labelEnd + 1 < text.Length &&
                    text[labelEnd + 1] == '(')
                {
                    int urlEnd = text.IndexOf(')', labelEnd + 2);

                    if (urlEnd > labelEnd + 2 &&
                        TryNormalizeUrl(
                            text[(labelEnd + 2)..urlEnd],
                            out string url))
                    {
                        FlushPlain();
                        tokens.Add(new InlineToken(
                            text[(index + 1)..labelEnd],
                            LinkStyle(baseStyle, url)));
                        index = urlEnd + 1;
                        continue;
                    }
                }
            }

            if (character is 'h' or 'H' &&
                TryReadBareUrl(text, index, out int urlLength, out string link))
            {
                FlushPlain();
                tokens.Add(new InlineToken(
                    text.Substring(index, urlLength),
                    LinkStyle(baseStyle, link)));
                index += urlLength;
                continue;
            }

            plain.Append(character);
            index++;
        }

        FlushPlain();
        return tokens;
    }

    private static int FindItalicClose(
        string text,
        int start,
        char marker)
    {
        for (int index = start; index < text.Length; index++)
        {
            if (text[index] != marker)
            {
                continue;
            }

            if (marker == '*')
            {
                if ((index > start && text[index - 1] == '*') ||
                    (index + 1 < text.Length && text[index + 1] == '*'))
                {
                    continue;
                }
            }
            else if (index + 1 < text.Length &&
                     char.IsLetterOrDigit(text[index + 1]))
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static bool TryReadBareUrl(
        string text,
        int start,
        out int length,
        out string url)
    {
        length = 0;
        url = "";

        if (!text.AsSpan(start).StartsWith(
                "http://",
                StringComparison.OrdinalIgnoreCase) &&
            !text.AsSpan(start).StartsWith(
                "https://",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int end = start;

        while (end < text.Length && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        int trimmed = end;

        while (trimmed > start &&
               ".,;:!?)]}>'\"".Contains(text[trimmed - 1]))
        {
            trimmed--;
        }

        if (trimmed <= start ||
            !TryNormalizeUrl(text[start..trimmed], out string normalized))
        {
            return false;
        }

        length = trimmed - start;
        url = normalized;
        return true;
    }

    private static bool TryNormalizeUrl(string value, out string url)
    {
        url = "";
        string trimmed = value.Trim().Trim('<', '>');

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp &&
            uri.Scheme != Uri.UriSchemeHttps &&
            uri.Scheme != Uri.UriSchemeMailto)
        {
            return false;
        }

        url = uri.AbsoluteUri;
        return true;
    }

    private static string LinkStyle(string baseStyle, string url) =>
        CombineStyles(
            baseStyle,
            $"underline {LinkHex} link={EscapeLinkTarget(url)}");

    private static string EscapeLinkTarget(string url) =>
        url
            .Replace("[", "%5B", StringComparison.Ordinal)
            .Replace("]", "%5D", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal);

    private static string CombineStyles(string baseStyle, string style) =>
        string.IsNullOrWhiteSpace(baseStyle)
            ? style
            : baseStyle + " " + style;
}
