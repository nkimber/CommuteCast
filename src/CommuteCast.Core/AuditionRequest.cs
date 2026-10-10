namespace CommuteCast.Core;

/// <summary>Immutable audition input captured before asynchronous preparation or scheduler admission.</summary>
public sealed record AuditionRequest
{
    public const int MaximumCharacters = 900;
    public const string StandardSample = "Welcome to CommuteCast. The API processes 24 requests per second. Version 2.10 costs 12.50 dollars. The measurement is 1.25e-3. Dates: 2026-10-06 and 03/04/2026.";
    public string Source { get; }
    public int? SelectionStart { get; }
    public NarrationSettings Settings { get; }
    private readonly string? context;
    private readonly bool approvedExcerpt;

    private AuditionRequest(string source, int? start, string? context, NarrationSettings settings, bool approvedExcerpt = false)
    { Source = source; SelectionStart = start; this.context = context; Settings = settings; this.approvedExcerpt = approvedExcerpt; }

    /// <summary>Preview an already prepared selection exactly once, without repeating dictionary/profile transformations.</summary>
    public static AuditionRequest ApprovedExcerpt(string text, NarrationSettings settings)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumCharacters)
            throw new ArgumentException($"Select 1–{MaximumCharacters} characters of spoken text. Nothing is truncated.");
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (++i == text.Length || !char.IsLowSurrogate(text[i])) throw new ArgumentException("Select complete Unicode characters.");
            }
            else if (char.IsLowSurrogate(text[i])) throw new ArgumentException("Select complete Unicode characters.");
        }
        return new(text, null, null, Validate(settings), true);
    }

    public static AuditionRequest Standard(NarrationSettings settings) => new(StandardSample, null, null, Validate(settings));
    public static AuditionRequest Selection(string source, int start, int length, NarrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > TextPreparation.MaximumCharacters) throw new ArgumentException("The draft exceeds the supported input limit. Shorten it before auditioning a selection.");
        if (start < 0 || length < 1 || start > source.Length || length > source.Length - start)
            throw new ArgumentException("Select a short excerpt in your text before playing it.");
        if (length > MaximumCharacters) throw new ArgumentException($"Select at most {MaximumCharacters} characters for a short audition. The selection was not shortened.");
        var end = start + length;
        if (SplitsCharacter(source, start) || SplitsCharacter(source, end))
            throw new ArgumentException("The selection cuts a Unicode character in half. Select the whole character and try again.");
        var excerpt = source.Substring(start, length);
        if (string.IsNullOrWhiteSpace(excerpt)) throw new ArgumentException("Select some spoken text for the audition.");
        for (var i = 0; i < excerpt.Length; i++)
        {
            if (char.IsHighSurrogate(excerpt[i]))
            {
                if (i + 1 == excerpt.Length || !char.IsLowSurrogate(excerpt[++i])) throw new ArgumentException("The selection contains an incomplete Unicode character. Select valid text and try again.");
            }
            else if (char.IsLowSurrogate(excerpt[i])) throw new ArgumentException("The selection contains an incomplete Unicode character. Select valid text and try again.");
        }
        var captured = Validate(settings);
        return new(excerpt, start, captured.ExcludeCode ? source : null, captured);
    }
    private static bool SplitsCharacter(string text, int boundary) => boundary > 0 && boundary < text.Length && char.IsHighSurrogate(text[boundary - 1]) && char.IsLowSurrogate(text[boundary]);
    private static NarrationSettings Validate(NarrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!SpeechProviders.IsKnown(settings.Engine) || string.IsNullOrWhiteSpace(settings.Voice) || !double.IsFinite(settings.Speed) || settings.Speed is < .7 or > 1.4)
            throw new ArgumentException("Choose an installed speech engine, voice and supported pace before auditioning.");
        settings.Speech?.Validate(settings.Engine); settings.Profile?.Validate(settings.Engine); TextPreparation.ValidateDictionary(settings.Pronunciation);
        return settings with { ProviderFingerprint = "", ProviderImageId = null };
    }
    public PreparedText Prepare(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (approvedExcerpt) return new(Source, [new(0, Source.Length, "approved spoken excerpt", Source, Source)], "audition-script-v1");
        // A cropped code body has lost its opening fence. Do not speak excluded code merely
        // because the fence sits outside the selection. Reuse the source preparer's classification.
        if (context is not null && SelectionStart is { } start)
        {
            var containing = TextPreparation.Prepare(context, ct: ct).Spans.First(s => start >= s.Start && start < s.Start + s.Length);
            if (containing.Kind == "spoken code" || containing.Narration == "End code block.\n" ||
                containing.Narration == "Code block.\n" && !context.AsSpan(containing.Start, start - containing.Start).Trim().IsEmpty)
                throw new ArgumentException("The selection starts inside excluded code. Select a complete passage outside the code block, or enable spoken code.");
        }
        var prepared = TextPreparation.Prepare(Source, Settings.ExcludeCode, Settings.Pronunciation, Settings.Profile, ct);
        if (prepared.Script.Length > MaximumCharacters)
            throw new ArgumentException($"Preparation expands this audition beyond {MaximumCharacters} characters. Select a shorter excerpt or shorten dictionary replacements. Nothing was truncated.");
        return prepared;
    }
}
