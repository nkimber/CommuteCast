using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Core;

public sealed record SpeakerPersonality(string Id, string Name, string Expertise, string Style, string Delivery = "", string Emotion = "");
public sealed record SpeakerVoiceBinding(string PersonalityId, string Provider, string Model, string Voice, double Speed, string Delivery, string Emotion,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LocalVoiceOptions? LocalVoice = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Pronunciation = null);
public sealed record PodcastFormat(string Id, string Name, int Hosts, int MinimumGuests, int MaximumGuests, string Structure)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 100 || Hosts is < 1 or > 2 || MinimumGuests < 0 || MaximumGuests < MinimumGuests || Hosts + MinimumGuests < 2 || Hosts + MaximumGuests > 5 || Structure.Length > 2000)
            throw new ArgumentException("A podcast format needs 2–5 participants, one or two hosts, and a short structure description.");
    }
}
public sealed record PodcastSpeaker(string Name, string Role, string Expertise, string Style, string Voice, double Speed = 1, string Delivery = "", string Emotion = "",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LocalVoiceOptions? LocalVoice = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Pronunciation = null);
public sealed record PodcastTurn(string Speaker, string Text, int? SourceStart = null, int? SourceLength = null);
public sealed record PodcastEpisode(PodcastFormat Format, IReadOnlyList<PodcastSpeaker> Speakers, bool Dialogue = true, int Version = 1)
{
    [System.Text.Json.Serialization.JsonIgnore] public string Identity => Job.Hash(JsonSerializer.Serialize(this));
    public void ValidateStorage()
    {
        if (Format is null || Speakers is null || Speakers.Count is < 2 or > 5 || Format.Name is null || Format.Name.Length > 100 || Format.Structure is null || Format.Structure.Length > 2000 ||
            Speakers.Any(s => s is null || s.Name is null || s.Name.Length > 40 || s.Role is null || s.Role.Length > 20 || s.Expertise is null || s.Expertise.Length > 1000 || s.Style is null || s.Style.Length > 1000 || s.Voice is null || s.Voice.Length > 200 || s.Delivery is null || s.Delivery.Length > 2000 || s.Emotion is null || s.Emotion.Length > 100 || s.Pronunciation?.Length > 16000))
            throw new ArgumentException("The saved podcast draft is invalid or too large.");
    }
    public void Validate(bool requireVoices = true)
    {
        ValidateStorage();
        Format.Validate();
        if (Version != 1 || Speakers.Count is < 2 or > 5 || Speakers.Count < Format.Hosts + Format.MinimumGuests || Speakers.Count > Format.Hosts + Format.MaximumGuests)
            throw new ArgumentException("The cast must fit the selected format and contain 2–5 speakers.");
        if (Speakers.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Speakers.Count)
            throw new ArgumentException("Give every speaker a unique name.");
        foreach (var s in Speakers)
        {
            if (!Regex.IsMatch(s.Name ?? "", @"^[\p{L}][\p{L}\p{N} _'-]{0,39}$") || s.Role is not ("Host" or "Guest") || s.Expertise.Length > 1000 || s.Style.Length > 1000 || s.Delivery.Length > 2000 || !double.IsFinite(s.Speed) || s.Speed is < .7 or > 1.4 || !SpeechProviders.Emotions.Contains(s.Emotion))
                throw new ArgumentException("Check speaker names, roles, descriptions and pace. Names must start with a letter and contain no colon.");
            if (requireVoices && string.IsNullOrWhiteSpace(s.Voice)) throw new ArgumentException($"Choose a voice for {s.Name}.");
        }
        if (Speakers.Count(s => s.Role == "Host") != Format.Hosts) throw new ArgumentException($"This format requires {Format.Hosts} host(s).");
    }
}
public static class PodcastDefaults
{
    public static IReadOnlyList<SpeakerPersonality> Personalities { get; } =
    [new("host", "Alex", "Connecting ideas and explaining unfamiliar subjects", "Warm, curious host; asks concise questions and keeps the discussion moving."),
     new("analyst", "Casey", "Evidence, systems and practical implications", "Clear, thoughtful analyst; uses concrete examples and explains uncertainty."),
     new("skeptic", "Jordan", "Testing assumptions and exploring tradeoffs", "Constructive skeptic; challenges claims politely and avoids manufactured disagreement."),
     new("practitioner", "Sam", "Practical applications and implementation", "Grounded practitioner; connects concepts to realistic situations."),
     new("context", "Morgan", "History and broader context", "Reflective specialist; explains how developments connect over time.")];
    public static IReadOnlyList<PodcastFormat> Formats { get; } =
    [new("cohosts", "Two presenters", 2, 0, 0, "Two hosts develop a connected explanation, alternating questions, examples and synthesis."),
     new("interview", "Host and guest", 1, 1, 1, "A curious host interviews a knowledgeable fictional guest, progressing from fundamentals to implications."),
     new("panel", "Host and expert panel", 1, 2, 4, "One host moderates complementary expert perspectives, invites follow-up questions and draws together the discussion."),
     new("guests", "Two presenters and guests", 2, 1, 3, "Two presenters guide a discussion with guests, distributing useful contributions without mechanical round-robin turns.")];
}

public static class PodcastScript
{
    public const string ChunkVersion = "podcast-block1800-v1";
    public const string AudioVersion = "podcast-pcm24k-loudnorm-gap80-v1";
    public const string NaturalChunkVersion = "podcast-natural900-v2";
    public const string NaturalAudioVersion = "podcast-pcm24k-natural-loudnorm-v2";
    public static LocalVoiceOptions? LocalDelivery(PodcastSpeaker speaker, NarrationSettings settings) =>
        SpeechProviders.IsHosted(settings.Engine) || settings.LocalVoice is null && speaker.LocalVoice is null ? null :
        (speaker.LocalVoice ?? new()) with { NaturalPhrasing = settings.LocalVoice?.NaturalPhrasing == true };
    public static string PronunciationFor(PodcastSpeaker speaker, NarrationSettings settings)
    {
        var rules = TextPreparation.ParseDictionary(settings.Pronunciation).ToDictionary(r => r.From, r => r.To);
        foreach (var (term, spoken) in TextPreparation.ParseDictionary(speaker.Pronunciation ?? "")) rules[term] = spoken;
        return string.Join("\n", rules.Select(r => r.Key + "=" + r.Value));
    }
    public static IReadOnlyList<PodcastTurn> Parse(string source, PodcastEpisode episode)
    {
        episode.Validate(false);
        if (string.IsNullOrWhiteSpace(source) || source.Length > TextPreparation.MaximumCharacters) throw new ArgumentException("Paste a dialogue within the supported text limit.");
        for (var i = 0; i < source.Length; i++)
        {
            if (char.IsHighSurrogate(source[i])) { if (++i == source.Length || !char.IsLowSurrogate(source[i])) throw new ArgumentException("The dialogue contains an incomplete Unicode character."); }
            else if (char.IsLowSurrogate(source[i])) throw new ArgumentException("The dialogue contains an incomplete Unicode character.");
        }
        var turns = new List<PodcastTurn>();
        var names = episode.Speakers.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim(); if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0 || !names.Contains(line[..colon])) throw new ArgumentException($"Line {i + 1}: expected an exact cast name followed by a colon and spoken dialogue. Remove preambles, references and headings.");
            var text = line[(colon + 1)..].Trim();
            if (text.Length == 0 || Regex.IsMatch(text, @"https?://|\[[^\]]*\]|<[^>]*>|\((laughs|pause|sighs|whispers|music)[^)]*\)|\*\*|```|^\s*(sources|references|bibliography)\b", RegexOptions.IgnoreCase))
                throw new ArgumentException($"Line {i + 1}: remove empty turns, URLs, citations, stage directions and Markdown before generating speech.");
            turns.Add(new(line[..colon], text));
        }
        if (turns.Count < 2 || names.Any(n => !turns.Any(t => t.Speaker == n))) throw new ArgumentException("Every cast member must have at least one spoken turn.");
        return turns;
    }
    public static (PreparedText Prepared, List<TextChunk> Units) Prepare(string source, PodcastEpisode episode, NarrationSettings settings)
    {
        episode.Validate(); settings.Speech?.Validate(settings.Engine);
        foreach (var speaker in episode.Speakers)
        {
            if (SpeechProviders.IsHosted(settings.Engine) && speaker.LocalVoice is not null) throw new ArgumentException("This cast has local voice controls. Choose a local provider or clear those controls.");
            LocalDelivery(speaker, settings)?.Validate(settings.Engine, speaker.Voice);
            TextPreparation.ValidateDictionary(speaker.Pronunciation ?? "");
            if (settings.Speech is { } speech) (speech with { Delivery = speaker.Delivery, Emotion = speaker.Emotion }).Validate(settings.Engine);
            else if (speaker.Delivery.Length > 0 || speaker.Emotion.Length > 0) throw new ArgumentException($"{speaker.Name}: this local provider supports voice and pace controls.");
            if (settings.Engine == "elevenlabs" && speaker.Speed > 1.2) throw new ArgumentException($"{speaker.Name}: ElevenLabs supports pace up to 1.2.");
            if (settings.Engine == "gemini" && speaker.Speed != 1) throw new ArgumentException($"{speaker.Name}: Gemini uses delivery instructions for pace. Set pace to 1.0.");
        }
        var parsed = Parse(source, episode);
        var capabilities = SpeechProviders.Capabilities(settings.Engine, settings.Speech?.Model);
        var joint = episode.Dialogue && settings.Speech?.Dialogue == true && capabilities.DialogueSpeakers >= episode.Speakers.Count && episode.Speakers.All(s => s.Speed == 1);
        int TagCharacters(string speaker) { var emotion = episode.Speakers.Single(s => s.Name == speaker).Emotion; return settings.Engine == "elevenlabs" && emotion is not ("" or "neutral") ? emotion.Length + 3 : 0; }
        var natural = settings.LocalVoice is { NaturalPhrasing: true } && !SpeechProviders.IsHosted(settings.Engine);
        var maximum = joint ? 1800 - episode.Speakers.Max(s => TagCharacters(s.Name)) : natural ? 900 : 450;
        var units = new List<TextChunk>(); var script = new StringBuilder(); var current = new List<PodcastTurn>(); var spans = new List<SourceSpan>(); var changes = new List<PronunciationChange>(); var currentHard = false;
        void Flush()
        {
            if (current.Count == 0) return;
            var text = string.Concat(current.Select(t => t.Text));
            units.Add(new(units.Count, script.Length, text.Length, text, currentHard, current.ToArray())); script.Append(text); current.Clear(); currentHard = false;
        }
        var ordinal = 0;
        foreach (Match line in Regex.Matches(source, @"[^\r\n]*(?:\r\n|\r|\n|$)"))
        {
            if (line.Length == 0) continue;
            if (string.IsNullOrWhiteSpace(line.Value)) { spans.Add(new(line.Index, line.Length, "podcast formatting", line.Value, "")); continue; }
            var turn = parsed[ordinal++]; var speaker = episode.Speakers.Single(s => s.Name == turn.Speaker);
            var preparedTurn = TextPreparation.Prepare(turn.Text, settings.ExcludeCode, PronunciationFor(speaker, settings), settings.Profile);
            var text = preparedTurn.Script.Trim() + "\n";
            if (script.Length + current.Sum(t => t.Text.Length) + text.Length > TextPreparation.MaximumCharacters) throw new ArgumentException("The prepared podcast exceeds the supported text limit. Shorten the dialogue or dictionary expansions.");
            spans.Add(new(line.Index, line.Length, "podcast dialogue · " + turn.Speaker, line.Value, text));
            var bodyStart = line.Value.IndexOf(':') + 1; while (bodyStart < line.Length && char.IsWhiteSpace(line.Value[bodyStart])) bodyStart++;
            if (preparedTurn.ProfileReview is { } turnReview) changes.AddRange(turnReview.Changes.Select(c => c with { SourceStart = c.SourceStart + line.Index + bodyStart }));
            foreach (var part in natural ? NaturalChunker.Split(text, maximum) : Chunker.Split(text, maximum))
            {
                if (!joint || current.Count >= 16 || current.Sum(t => t.Text.Length + TagCharacters(t.Speaker)) + part.Length + TagCharacters(turn.Speaker) > 1800) Flush();
                current.Add(new(turn.Speaker, part.Text, line.Index + bodyStart, turn.Text.Length)); currentHard = natural && part.HardSplit;
                if (!joint) Flush();
            }
        }
        Flush();
        var review = settings.Profile is null ? null : new PronunciationReview(settings.Profile, TextPreparation.DictionaryRevision(settings.Pronunciation), changes);
        return (new(script.ToString(), spans, "podcast-prepare-v1", review), units);
    }
    public static void ValidateManifest(Job job)
    {
        var episode = job.Episode ?? throw new ArgumentException("Missing podcast cast."); episode.Validate();
        if ((!NaturalChunker.IsNatural(job) && (job.ChunkingVersion != ChunkVersion || job.AudioContractVersion != AudioVersion)) || job.Prepared.Version != "podcast-prepare-v1") throw new ArgumentException("Unsupported podcast render contract.");
        if (string.Concat(job.Prepared.Spans.Select(s => s.Original)) != job.Source || string.Concat(job.Prepared.Spans.Select(s => s.Narration)) != job.Prepared.Script) throw new ArgumentException("Podcast source coverage differs from its prepared script.");
        Chunker.ValidateManifest(job.Chunks, job.Prepared.Script, NaturalChunker.IsNatural(job) ? 900 : 1800);
        foreach (var unit in job.Chunks)
        {
            if (unit.Turns is null || unit.Turns.Count == 0 || string.Concat(unit.Turns.Select(t => t.Text)) != unit.Text || unit.Turns.Any(t => !episode.Speakers.Any(s => s.Name == t.Speaker)))
                throw new ArgumentException("Podcast voice assignments differ from the prepared script.");
        }
    }
    public static string Prompt(NarrationBrief brief, PodcastEpisode episode, DateOnly date)
    {
        episode.Validate(false);
        var result = NarrationPrompt.Build(brief, date).Replace("Never invent quotations, citations, statistics, dialogue, motives", "Never invent quotations, citations, statistics, motives").Replace(NarrationPrompt.OutputInstructions, "Return only dialogue, one complete turn per line, in the exact format Speaker: spoken words. Use only the cast names below with exact spelling. No preamble, title, headings, citations, URLs, references, source list, stage directions, emotion tags, production instructions or closing commentary. Every line will be spoken after its speaker label is removed. Include no text outside those dialogue lines.");
        var text = new StringBuilder(result).AppendLine().AppendLine("PODCAST CAST AND FORMAT");
        text.AppendLine($"Format: {episode.Format.Name}. {episode.Format.Structure}");
        foreach (var s in episode.Speakers) text.AppendLine($"{s.Name} ({s.Role}). Expertise: {s.Expertise}. Speaking personality: {s.Style}.");
        text.AppendLine("These are fictional presenters and guests, not impersonations or real interviews. Write a natural conversation: speakers respond to what was just said, ask useful follow-up questions and contribute different perspectives. Vary turn lengths, avoid repeated agreement and filler, and avoid artificial disputes. Include every cast member meaningfully. Do not have speakers introduce their expertise as credentials or claim personal experiences as facts. Keep factual uncertainty and necessary attribution within the conversation. Privately check exact speaker labels and remove all non-dialogue text before responding.");
        return text.ToString();
    }
}
