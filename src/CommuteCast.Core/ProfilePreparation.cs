using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Core;

// Positions refer to UTF-16 offsets in the retained source, including CRLF and surrogate pairs.
internal static class ProfilePreparation
{
    private const int MaximumScript = 2_000_000;
    private readonly record struct Origin(int Start, int Length, bool Protected = false);
    private sealed class ChangeLog : List<PronunciationChange>
    {
        private long remainingCharacters = 8_000_000;
        public void Record(Origin origin, Match match, string value, string rule, string source, bool warning)
        {
            var size = (long)origin.Length + match.Length + value.Length;
            if (Count >= 100_000 || size > remainingCharacters)
                throw new ArgumentException("Pronunciation produces too many review changes. Shorten the source or cascading dictionary; your draft is retained.");
            remainingCharacters -= size;
            Add(new(origin.Start, origin.Length, source.Substring(origin.Start, origin.Length), match.Value, value, rule, warning));
        }
    }
    private sealed record Mapped(string Text, Origin[] Origins)
    {
        public Mapped Slice(int start, int length) => new(Text.Substring(start, length), Origins.AsSpan(start, length).ToArray());
        public Mapped Trim(params char[] chars)
        {
            var start = 0; var end = Text.Length;
            bool Match(char c) => chars.Length == 0 ? char.IsWhiteSpace(c) : chars.Contains(c);
            while (start < end && Match(Text[start])) start++;
            while (end > start && Match(Text[end - 1])) end--;
            return Slice(start, end - start);
        }
    }
    private sealed class Builder
    {
        private readonly StringBuilder text = new(); private readonly List<Origin> origins = [];
        public void Append(Mapped part)
        {
            Ensure(part.Text.Length); text.Append(part.Text); origins.AddRange(part.Origins);
        }
        public void Append(string value, Origin origin)
        {
            Ensure(value.Length); text.Append(value);
            for (var i = 0; i < value.Length; i++) origins.Add(origin);
        }
        private void Ensure(int length)
        {
            if (length > MaximumScript - text.Length) throw new ArgumentException("Pronunciation expands the script beyond 2,000,000 characters. Shorten the dictionary or source; your draft is retained.");
        }
        public Mapped Build() => new(text.ToString(), origins.ToArray());
    }
    private static Mapped Cue(string text) => new(text, Enumerable.Repeat(new Origin(-1, 0, true), text.Length).ToArray());
    private static Mapped Join(params Mapped[] parts) { var b = new Builder(); foreach (var p in parts) b.Append(p); return b.Build(); }
    private static Mapped Replace(Mapped input, string pattern, Func<Match, Mapped> replace, CancellationToken ct)
    {
        var matches = Regex.Matches(input.Text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        if (matches.Count == 0) return input;
        var b = new Builder(); var offset = 0;
        foreach (Match m in matches)
        {
            ct.ThrowIfCancellationRequested(); b.Append(input.Slice(offset, m.Index - offset)); b.Append(replace(m)); offset = m.Index + m.Length;
        }
        b.Append(input.Slice(offset, input.Text.Length - offset)); return b.Build();
    }
    public static IReadOnlyList<(string From, string To)> ValidateDictionary(string dictionary)
    {
        if (dictionary.Length > 16_384) throw new ArgumentException("The pronunciation dictionary limit is 16,384 characters.");
        var rules = TextPreparation.ParseDictionary(dictionary);
        if (rules.Count > 256 || rules.Any(r => r.From.Length > 128 || r.To.Length > 1024))
            throw new ArgumentException("Use at most 256 dictionary entries, with terms up to 128 and replacements up to 1,024 characters.");
        return rules;
    }
    public static string DictionaryRevision(string dictionary) => Job.Hash(JsonSerializer.Serialize(ValidateDictionary(dictionary).Select(r => new { r.From, r.To })));
    public static PreparedText Prepare(string source, bool excludeCode, string dictionary, PronunciationProfile profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); profile.Validate();
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Paste some text before queueing.");
        if (source.Length > TextPreparation.MaximumCharacters) throw new ArgumentException($"The input limit is {TextPreparation.MaximumCharacters:N0} characters. Your draft is retained.");
        var rules = ValidateDictionary(dictionary); var changes = new ChangeLog(); var spans = new List<SourceSpan>();
        var script = new Builder(); string? fence = null;
        foreach (Match line in Regex.Matches(source, @"[^\n]*\n|[^\n]+$", RegexOptions.CultureInvariant))
        {
            ct.ThrowIfCancellationRequested(); if (line.Length == 0) continue;
            var original = line.Value; var raw = new Mapped(original, Enumerable.Range(line.Index, line.Length).Select(i => new Origin(i, 1)).ToArray());
            var body = raw.Slice(0, original.TrimEnd('\r', '\n').Length); var marker = Regex.Match(body.Text, @"^\s{0,3}(`{3,}|~{3,})(.*)$");
            var closing = fence is not null && marker.Success && marker.Groups[1].Value[0] == fence[0] && marker.Groups[1].Length >= fence.Length && string.IsNullOrWhiteSpace(marker.Groups[2].Value);
            Mapped spoken; string kind;
            if (fence is null && marker.Success || closing)
            {
                fence = closing ? null : marker.Groups[1].Value;
                kind = excludeCode ? "explicit exclusion" : "formatting";
                spoken = Cue(excludeCode ? "\n" : fence is not null ? "Code block.\n" : "End code block.\n");
            }
            else if (fence is not null)
            {
                kind = excludeCode ? "explicit exclusion" : "spoken code";
                spoken = excludeCode ? Cue("\n") : Join(Technical(body, ct), Cue("\n"));
            }
            else if (Regex.IsMatch(body.Text, @"^\s*\|?\s*:?-{3,}:?\s*(?:\|\s*:?-{3,}:?\s*)+\|?\s*$|^\s*(?:-{3,}|\*{3,}|_{3,})\s*$"))
            { spoken = Cue("\n"); kind = "formatting"; }
            else
            {
                spoken = Replace(raw, @"^\s{0,3}#{1,6}\s+", _ => Cue(""), ct);
                spoken = Replace(spoken, @"^\s*(?:[-+*]|>)\s+", _ => Cue(""), ct);
                var input = spoken;
                spoken = Replace(input, @"!?\[([^\]]+)\]\(([^)]+)\)", m => Join(input.Slice(m.Groups[1].Index, m.Groups[1].Length), Cue(" ("), input.Slice(m.Groups[2].Index, m.Groups[2].Length), Cue(")")), ct);
                input = spoken;
                spoken = Replace(input, @"(?<!\w)(\*\*|__)(?=\S)((?:(?!\1).)+?)(?<=\S)\1(?!\w)", m => input.Slice(m.Groups[2].Index, m.Groups[2].Length), ct);
                input = spoken;
                spoken = Replace(input, @"(`+)([^`]+)\1", m => Technical(input.Slice(m.Groups[2].Index, m.Groups[2].Length), ct), ct);
                if (body.Text.TrimStart().StartsWith('|'))
                {
                    input = spoken.Trim().Trim('|'); var b = new Builder(); b.Append(Cue("Table row. ")); var offset = 0; var column = 1;
                    foreach (var cell in input.Text.Split('|'))
                    {
                        if (column > 1) b.Append(Cue("; "));
                        b.Append(Cue($"Column {column++}: ")); var mapped = input.Slice(offset, cell.Length).Trim();
                        b.Append(mapped.Text.Length == 0 ? Cue("empty cell") : mapped); offset += cell.Length + 1;
                    }
                    b.Append(Cue(".\n")); spoken = b.Build();
                }
                input = spoken;
                spoken = Replace(input, @"[^\S\r\n]+", m => new(" ", [Cover(input, m.Index, m.Length)]), ct);
                kind = spoken.Text == original ? "spoken" : "formatting + spoken";
            }
            var initialChanges = changes.Count;
            foreach (var (from, to) in rules)
            {
                var input = spoken;
                spoken = Replace(input, @"(?<![\p{L}\p{N}_])" + Regex.Escape(from) + @"(?![\p{L}\p{N}_])", m => Transform(input, m, to, "Dictionary: " + from, source, changes, true), ct);
            }
            if (profile.Dates != DateReading.NoCalendarInterpretation)
            {
                var input = spoken;
                var pattern = profile.Dates == DateReading.IsoYearMonthDay ? @"(?<![\p{L}\p{N}_])\d{4}-\d{2}-\d{2}(?![\p{L}\p{N}_-])" : @"(?<![\p{L}\p{N}_/])\d{1,2}/\d{1,2}/\d{4}(?![\p{L}\p{N}_/])";
                spoken = Replace(input, pattern, m =>
                {
                    if (IsProtected(input, m)) return input.Slice(m.Index, m.Length);
                    var calendar = EnglishNumbers.Calendar(m.Value, profile.Dates);
                    return Transform(input, m, calendar ?? m.Value, calendar is null ? "Invalid calendar date; kept as written" : "Date: " + profile.Dates, source, changes, true, calendar is null);
                }, ct);
            }
            if (profile.Numbers != NumberReading.AsWritten)
            {
                var input = spoken;
                spoken = Replace(input, @"[+\-−]?[0-9]+(?:[.,:/-][0-9]+)*(?:[eE][+\-]?[0-9]+)?", m =>
                {
                    if (IsProtected(input, m)) return input.Slice(m.Index, m.Length);
                    var value = profile.Numbers == NumberReading.LiteralDigits ? EnglishNumbers.Literal(m.Value) : EnglishNumbers.Words(m.Value, profile.Numbers == NumberReading.ScientificWords);
                    // Separate identifier/version prefixes and adjacent operators from their verbalization.
                    if (m.Index > 0 && char.IsLetterOrDigit(input.Text[m.Index - 1])) value = " " + value;
                    if (m.Index + m.Length < input.Text.Length && char.IsLetterOrDigit(input.Text[m.Index + m.Length])) value += " ";
                    return Transform(input, m, value, "Numbers: " + profile.Numbers, source, changes, true);
                }, ct);
            }
            if (profile.Acronyms == AcronymReading.SpellUppercaseWords)
            {
                var input = spoken;
                spoken = Replace(input, @"(?<![\p{L}\p{N}_])[A-Z]{2,32}(?![\p{L}\p{N}_])", m => IsProtected(input, m) ? input.Slice(m.Index, m.Length) : Transform(input, m, string.Join(" ", m.Value.ToCharArray()), "Spell uppercase word", source, changes, true), ct);
            }
            if (changes.Count > initialChanges) kind += " + pronunciation";
            spans.Add(new(line.Index, line.Length, kind, original, spoken.Text)); script.Append(spoken);
        }
        var final = script.Build().Text;
        if (string.IsNullOrWhiteSpace(final)) throw new ArgumentException("Preparation left no spoken content. Include code or add text.");
        return new(final, spans, "prepare-v3", new(profile, DictionaryRevision(dictionary), changes));
    }
    private static Origin Cover(Mapped input, int start, int length)
    {
        var min = int.MaxValue; var max = -1; var protect = false;
        for (var i = start; i < start + length; i++)
        {
            var o = input.Origins[i]; protect |= o.Protected;
            if (o.Start < 0) continue; min = Math.Min(min, o.Start); max = Math.Max(max, o.Start + o.Length);
        }
        return max < 0 ? new(-1, 0, true) : new(min, max - min, protect);
    }
    private static bool IsProtected(Mapped input, Match m) => Cover(input, m.Index, m.Length).Protected;
    private static Mapped Transform(Mapped input, Match m, string value, string rule, string source, ChangeLog changes, bool protect, bool warning = false)
    {
        var origin = Cover(input, m.Index, m.Length);
        if (origin.Start < 0) return input.Slice(m.Index, m.Length);
        changes.Record(origin, m, value, rule, source, warning);
        var b = new Builder(); b.Append(value, origin with { Protected = protect }); return b.Build();
    }
    private static Mapped Technical(Mapped input, CancellationToken ct)
    {
        const string symbols = "_=+-*/\\<>!&|(){}[];:#%\"'`";
        string[] words = ["underscore", "equals", "plus", "dash", "asterisk", "slash", "backslash", "less than", "greater than", "exclamation", "ampersand", "pipe", "open parenthesis", "close parenthesis", "open brace", "close brace", "open bracket", "close bracket", "semicolon", "colon", "hash", "percent", "quote", "apostrophe", "backtick"];
        var b = new Builder();
        for (var i = 0; i < input.Text.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var index = symbols.IndexOf(input.Text[i]);
            if (index < 0) b.Append(input.Slice(i, 1)); else b.Append(" " + words[index] + " ", input.Origins[i]);
        }
        var expanded = b.Build();
        return Replace(expanded, @"[^\S\r\n]+", m => new(" ", [Cover(expanded, m.Index, m.Length)]), ct).Trim();
    }
}
