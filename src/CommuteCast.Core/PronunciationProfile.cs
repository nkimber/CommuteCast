using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Core;

public enum NumberReading { AsWritten, LiteralDigits, NumberWords, ScientificWords }
public enum AcronymReading { AsWritten, SpellUppercaseWords }
public enum DateReading { NoCalendarInterpretation, IsoYearMonthDay, MonthDayYear, DayMonthYear }

public sealed record PronunciationProfile(string Version = "pronunciation-v2", string DictionaryVersion = "literal-dictionary-v1",
    NumberReading Numbers = NumberReading.AsWritten, AcronymReading Acronyms = AcronymReading.AsWritten,
    DateReading Dates = DateReading.NoCalendarInterpretation, string Language = "en")
{
    public void Validate(string? engine = null)
    {
        if (Version != "pronunciation-v2" || DictionaryVersion != "literal-dictionary-v1") throw new ArgumentException("This pronunciation profile version is unsupported. Choose the supported English profile for a new narration.");
        if (Language != "en") throw new ArgumentException("The installed pronunciation profiles support English. Other languages require a verified model and profile.");
        if (!Enum.IsDefined(Numbers) || !Enum.IsDefined(Acronyms) || !Enum.IsDefined(Dates)) throw new ArgumentException("Choose supported number, acronym and date options.");
        if (engine is not null && engine is not ("kokoro" or "piper")) throw new ArgumentException("Pronunciation options require a supported Kokoro or Piper speech contract.");
    }
}
public sealed record PronunciationChange(int SourceStart, int SourceLength, string Original, string Before, string After, string Rule, bool Warning = false);
public sealed record PronunciationReview(PronunciationProfile Profile, string DictionaryRevision, IReadOnlyList<PronunciationChange> Changes);

internal static class EnglishNumbers
{
    private static readonly string[] Small = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
    private static readonly string[] Scale = ["", "thousand", "million", "billion", "trillion", "quadrillion"];
    private static readonly string[] Ordinals = ["", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth", "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth", "eighteenth", "nineteenth", "twentieth", "twenty-first", "twenty-second", "twenty-third", "twenty-fourth", "twenty-fifth", "twenty-sixth", "twenty-seventh", "twenty-eighth", "twenty-ninth", "thirtieth", "thirty-first"];
    public static string Literal(string value) => string.Join(" ", value.Select(c => c switch
    { >= '0' and <= '9' => Small[c - '0'], '.' => "point", ',' => "comma", '/' => "slash", ':' => "colon", '-' => "dash", '−' => "minus", '+' => "plus", 'e' or 'E' => "E", _ => c.ToString() }));
    public static string Words(string value, bool scientific)
    {
        if (scientific && Regex.Match(value, @"^([+\-−]?[0-9]+(?:\.[0-9]+)?)[eE]([+\-]?[0-9]+)$", RegexOptions.CultureInvariant) is { Success: true } exponent)
            return Words(exponent.Groups[1].Value, false) + " times ten to the power of " + Words(exponent.Groups[2].Value, false);
        if (!Regex.IsMatch(value, @"^[+\-−]?(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?:\.[0-9]+)?$", RegexOptions.CultureInvariant)) return Literal(value);
        var sign = value[0] is '-' or '−' ? "minus " : value[0] == '+' ? "plus " : "";
        value = value.TrimStart('+', '-', '−').Replace(",", ""); var parts = value.Split('.');
        var integer = parts[0].Length > 18 || parts[0].Length > 1 && parts[0][0] == '0' ? Literal(parts[0]) : Cardinal(ulong.Parse(parts[0], CultureInfo.InvariantCulture));
        return sign + integer + (parts.Length == 2 ? " point " + Literal(parts[1]) : "");
    }
    private static string Cardinal(ulong value)
    {
        if (value == 0) return "zero"; var groups = new List<string>(); var scale = 0;
        while (value > 0)
        {
            var group = (int)(value % 1000);
            if (group > 0)
            {
                var words = group >= 100 ? Small[group / 100] + " hundred" : ""; var rest = group % 100;
                if (rest > 0) words += (words.Length > 0 ? " " : "") + (rest < 20 ? Small[rest] : Tens[rest / 10] + (rest % 10 > 0 ? " " + Small[rest % 10] : ""));
                groups.Insert(0, words + (scale > 0 ? " " + Scale[scale] : ""));
            }
            value /= 1000; scale++;
        }
        return string.Join(" ", groups);
    }
    public static string? Calendar(string value, DateReading mode)
    {
        string[] formats = mode switch
        { DateReading.IsoYearMonthDay => ["yyyy-MM-dd"], DateReading.MonthDayYear => ["M/d/yyyy", "MM/dd/yyyy", "M/dd/yyyy", "MM/d/yyyy"], DateReading.DayMonthYear => ["d/M/yyyy", "dd/MM/yyyy", "d/MM/yyyy", "dd/M/yyyy"], _ => [] };
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        var month = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(date.Month);
        return mode == DateReading.DayMonthYear ? Ordinals[date.Day] + " of " + month + ", " + Cardinal((ulong)date.Year) : month + " " + Ordinals[date.Day] + ", " + Cardinal((ulong)date.Year);
    }
}
