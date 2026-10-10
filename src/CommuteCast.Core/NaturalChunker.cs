using System.Text.RegularExpressions;

namespace CommuteCast.Core;

public static class NaturalChunker
{
    public const string Version = "natural900-v2";
    public const string AudioVersion = "pcm24k-natural-loudnorm-v2";
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
        { "Mr.", "Mrs.", "Ms.", "Dr.", "Prof.", "St.", "Jr.", "Sr.", "vs.", "e.g.", "i.e.", "etc.", "a.m.", "p.m." };

    public static List<TextChunk> Split(string script, int maximum = 900)
    {
        if (maximum < 16 || maximum > 900) throw new ArgumentOutOfRangeException(nameof(maximum));
        var result = new List<TextChunk>(); var start = 0;
        while (start < script.Length)
        {
            var length = Math.Min(maximum, script.Length - start); var hard = false;
            var section = script.Substring(start, length);
            var paragraph = Regex.Match(section, @"\n[\t \r]*\n[\s]*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (paragraph.Success && paragraph.Index + paragraph.Length < section.Length)
                length = paragraph.Index + paragraph.Length;
            else if (start + length < script.Length)
            {
                var boundaries = Regex.Matches(section, @"[.!?](?:[""'’”\)\]]*)\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                var sentence = boundaries.LastOrDefault(m => m.Index + m.Length >= length / 3 && IsSentenceEnd(section, m.Index));
                if (sentence is not null) length = sentence.Index + sentence.Length;
                else
                {
                    hard = true;
                    var clause = Regex.Matches(section, @"[,;:]\s+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).LastOrDefault(m => m.Index >= length / 3);
                    var space = section.LastIndexOfAny([' ', '\t', '\n']);
                    if (clause is not null) length = clause.Index + clause.Length;
                    else if (space >= length / 3) length = space + 1;
                    else if (char.IsHighSurrogate(script[start + length - 1])) length--;
                }
            }
            result.Add(new(result.Count, start, length, script.Substring(start, length), hard)); start += length;
        }
        Chunker.ValidateManifest(result, script, maximum); return result;
    }
    private static bool IsSentenceEnd(string section, int punctuation)
    {
        if (section[punctuation] != '.') return true;
        var start = punctuation; while (start > 0 && (char.IsLetter(section[start - 1]) || section[start - 1] == '.')) start--;
        var token = section[start..(punctuation + 1)];
        return !Abbreviations.Contains(token) && !Regex.IsMatch(token, @"^(?:[A-Za-z]\.)+$", RegexOptions.CultureInvariant);
    }
    public static int PauseMilliseconds(TextChunk chunk, LocalVoiceOptions options)
    {
        if (chunk.HardSplit) return 0;
        if (Regex.IsMatch(chunk.Text, @"\n[\t \r]*\n\s*$", RegexOptions.CultureInvariant)) return options.ParagraphPauseMs;
        var end = chunk.Text.TrimEnd().TrimEnd('"', '\'', '’', '”', ')', ']');
        return end.EndsWith('.') || end.EndsWith('?') || end.EndsWith('!') ? options.SentencePauseMs : 0;
    }
    public static bool IsNatural(Job job) => job.Settings.LocalVoice is { NaturalPhrasing: true } && job.Settings.Engine is "kokoro" or "piper" &&
        (job.Episode is null ? job.AudioContractVersion == AudioVersion && job.ChunkingVersion == Version :
            job.AudioContractVersion == PodcastScript.NaturalAudioVersion && job.ChunkingVersion == PodcastScript.NaturalChunkVersion);

    public static void ConfigureNew(Job job)
    {
        if (job.Settings.LocalVoice is not { NaturalPhrasing: true } || job.Settings.Engine is not ("kokoro" or "piper")) return;
        job.Settings.LocalVoice.Validate(job.Settings.Engine, job.Settings.Voice);
        if (job.Receipts.Count > 0 || job.Episode is null && job.Chunks.Count > 0) throw new ArgumentException("Capture natural phrasing before generating any segments.");
        job.ChunkingVersion = job.Episode is null ? Version : PodcastScript.NaturalChunkVersion;
        job.AudioContractVersion = job.Episode is null ? AudioVersion : PodcastScript.NaturalAudioVersion;
    }
}
