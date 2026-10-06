using System.Text;
using System.Text.RegularExpressions;

namespace CommuteCast.Core;

public static partial class TextPreparation
{
    public const int MaximumCharacters = 250_000;
    public static void ValidateDictionary(string dictionary) => ProfilePreparation.ValidateDictionary(dictionary);
    public static string DictionaryRevision(string dictionary) => ProfilePreparation.DictionaryRevision(dictionary);
    public static PreparedText Prepare(string source, bool excludeCode = false, string pronunciation = "", PronunciationProfile? profile = null, CancellationToken ct = default)
    {
        if (profile is not null) return ProfilePreparation.Prepare(source, excludeCode, pronunciation, profile, ct);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Paste some text before queueing.");
        if (source.Length > MaximumCharacters) throw new ArgumentException($"The input limit is {MaximumCharacters:N0} characters. Your draft is retained.");
        var rules = ParseDictionary(pronunciation);
        var spans = new List<SourceSpan>();
        var script = new StringBuilder();
        string? fence = null;
        foreach (Match line in Lines().Matches(source))
        {
            if (line.Length == 0) continue;
            var original = line.Value;
            var body = original.TrimEnd('\r', '\n');
            var trimmed = body.TrimStart();
            string spoken;
            string kind;
            var marker = Regex.Match(body, @"^\s{0,3}(`{3,}|~{3,})(.*)$");
            var closingFence = fence is not null && marker.Success && marker.Groups[1].Value[0] == fence[0] && marker.Groups[1].Length >= fence.Length && string.IsNullOrWhiteSpace(marker.Groups[2].Value);
            if ((fence is null && marker.Success) || closingFence)
            {
                fence = closingFence ? null : marker.Groups[1].Value;
                kind = excludeCode ? "explicit exclusion" : "formatting";
                spoken = excludeCode ? "\n" : fence is not null ? "Code block.\n" : "End code block.\n";
            }
            else if (fence is not null)
            {
                spoken = excludeCode ? "\n" : VerbalizeTechnical(body) + "\n";
                kind = excludeCode ? "explicit exclusion" : "spoken code";
            }
            else if (TableDivider().IsMatch(body) || Rule().IsMatch(body))
            {
                spoken = "\n";
                kind = "formatting";
            }
            else
            {
                spoken = Heading().Replace(original, "");
                spoken = Bullet().Replace(spoken, "");
                spoken = Link().Replace(spoken, "$1 ($2)");
                spoken = Regex.Replace(spoken, @"(?<!\w)(\*\*|__)(?=\S)((?:(?!\1).)+?)(?<=\S)\1(?!\w)", "$2");
                spoken = Regex.Replace(spoken, @"(`+)([^`]+)\1", m => VerbalizeTechnical(m.Groups[2].Value));
                if (trimmed.StartsWith('|'))
                {
                    var cells = spoken.Trim().Trim('|').Split('|');
                    spoken = "Table row. " + string.Join("; ", cells.Select((cell, i) => $"Column {i + 1}: {(string.IsNullOrWhiteSpace(cell) ? "empty cell" : cell.Trim())}")) + ".\n";
                }
                spoken = Regex.Replace(spoken, @"[^\S\r\n]+", " ");
                kind = spoken == original ? "spoken" : "formatting + spoken";
            }
            foreach (var (from, to) in rules)
                spoken = Regex.Replace(spoken, @"(?<![\p{L}\p{N}_])" + Regex.Escape(from) + @"(?![\p{L}\p{N}_])", _ => to, RegexOptions.CultureInvariant);
            if (rules.Count > 0 && spoken != original && kind.StartsWith("spoken")) kind = "pronunciation + spoken";
            spans.Add(new(line.Index, line.Length, kind, original, spoken));
            script.Append(spoken);
        }
        if (string.IsNullOrWhiteSpace(script.ToString())) throw new ArgumentException("Preparation left no spoken content. Include code or add text.");
        if (spans.Sum(s => s.Length) != source.Length) throw new InvalidOperationException("Source accounting failed.");
        // Excess whitespace is formatting, including blank sections around explicitly excluded code.
        // Each span still records its exact original content and resulting narration.
        return new(script.ToString(), spans, "prepare-v2");
    }

    private static string VerbalizeTechnical(string text)
    {
        var symbols = new Dictionary<char, string> { ['_'] = " underscore ", ['='] = " equals ", ['+'] = " plus ", ['-'] = " dash ", ['*'] = " asterisk ", ['/'] = " slash ", ['\\'] = " backslash ", ['<'] = " less than ", ['>'] = " greater than ", ['!'] = " exclamation ", ['&'] = " ampersand ", ['|'] = " pipe ", ['('] = " open parenthesis ", [')'] = " close parenthesis ", ['{'] = " open brace ", ['}'] = " close brace ", ['['] = " open bracket ", [']'] = " close bracket ", [';'] = " semicolon ", [':'] = " colon ", ['#'] = " hash ", ['%'] = " percent ", ['\"'] = " quote ", ['\''] = " apostrophe ", ['`'] = " backtick " };
        return Regex.Replace(string.Concat(text.Select(c => symbols.TryGetValue(c, out var word) ? word : c.ToString())), @"[^\S\r\n]+", " ").Trim();
    }

    public static IReadOnlyList<(string From, string To)> ParseDictionary(string dictionary)
    {
        var result = new List<(string, string)>();
        foreach (var line in dictionary.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Pronunciation entries must use term=spoken words, one per line.");
            if (result.Any(r => r.Item1 == parts[0])) throw new ArgumentException("Each pronunciation term must be unique.");
            result.Add((parts[0], parts[1]));
        }
        return result;
    }

    public static string SuggestTitle(string source)
    {
        var heading = Regex.Match(source, @"(?m)^\s{0,3}#{1,6}\s+(.+)$");
        var text = heading.Success ? heading.Groups[1].Value : source.Split('\n').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "";
        text = text.Trim().Trim('#', '*', '`', ' ');
        var end = Regex.Match(text, @"[.!?](?:\s|$)");
        if (!heading.Success && end.Success) text = text[..(end.Index + 1)];
        return text.Length == 0 ? $"CommuteCast {DateTimeOffset.Now:yyyy-MM-dd HH:mm}" : text[..Math.Min(100, text.Length)];
    }

    [GeneratedRegex(@"[^\n]*\n|[^\n]+$", RegexOptions.CultureInvariant)] private static partial Regex Lines();
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+", RegexOptions.CultureInvariant)] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*(?:[-+*]|>)\s+", RegexOptions.CultureInvariant)] private static partial Regex Bullet();
    [GeneratedRegex(@"!?\[([^\]]+)\]\(([^)]+)\)", RegexOptions.CultureInvariant)] private static partial Regex Link();
    [GeneratedRegex(@"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?\s*$", RegexOptions.CultureInvariant)] private static partial Regex TableDivider();
    [GeneratedRegex(@"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.CultureInvariant)] private static partial Regex Rule();
}

public static class Chunker
{
    public static void ValidateManifest(IReadOnlyList<TextChunk> chunks, string script, int maximum = 450)
    {
        if (script.Length == 0 || chunks.Count == 0) throw new IOException("The narration manifest is empty. Publication is blocked.");
        var offset = 0;
        for (var ordinal = 0; ordinal < chunks.Count; ordinal++)
        {
            var chunk = chunks[ordinal];
            if (chunk.Index != ordinal || chunk.Start != offset || chunk.Length != chunk.Text.Length || chunk.Length is <= 0 || chunk.Length > maximum ||
                offset + chunk.Length > script.Length || string.CompareOrdinal(script, offset, chunk.Text, 0, chunk.Length) != 0 ||
                char.IsLowSurrogate(chunk.Text[0]) || char.IsHighSurrogate(chunk.Text[^1]))
                throw new IOException("The narration manifest has missing, duplicate, reordered, or invalid spans. Publication is blocked.");
            offset += chunk.Length;
        }
        if (offset != script.Length) throw new IOException("The narration manifest does not cover the complete script. Publication is blocked.");
    }
    public static List<TextChunk> Split(string script, int maximum = 900)
    {
        if (maximum < 16) throw new ArgumentOutOfRangeException(nameof(maximum));
        var chunks = new List<TextChunk>();
        var start = 0;
        while (start < script.Length)
        {
            var length = Math.Min(maximum, script.Length - start);
            var hard = false;
            if (start + length < script.Length)
            {
                var segment = script.Substring(start, length);
                var boundaries = Regex.Matches(segment, @"\n\s*\n|[.!?](?:[\""')\]]*)\s+|\n");
                var boundary = boundaries.LastOrDefault(m => m.Index + m.Length >= length / 3);
                if (boundary is not null) length = boundary.Index + boundary.Length;
                else
                {
                    hard = true;
                    var space = segment.LastIndexOfAny([' ', '\t']);
                    if (space >= length / 3) length = space + 1;
                    else
                    {
                        hard = true;
                        if (char.IsHighSurrogate(script[start + length - 1])) length--;
                    }
                }
            }
            chunks.Add(new(chunks.Count, start, length, script.Substring(start, length), hard));
            start += length;
        }
        return chunks;
    }
}
