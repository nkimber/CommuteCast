namespace CommuteCast.Core;

public sealed record NarrationPreset(string Name, NarrationOptions Options);

public sealed class NarrationPresets
{
    private readonly AppSettings settings;
    public IReadOnlyList<NarrationPreset> Items => settings.NarrationPresets!;
    public NarrationPresets(AppSettings settings)
    {
        this.settings = settings;
        settings.NarrationPresets ??=
        [
            new("Technical reading", new("kokoro", "af_heart", 1, true, "", new(Numbers: NumberReading.ScientificWords, Acronyms: AcronymReading.SpellUppercaseWords))),
            new("Relaxed storytelling", new("kokoro", "af_heart", .9, false, "", new(Numbers: NumberReading.NumberWords))),
            new("Fast briefing", new("piper", "en_US-lessac-medium", 1.2, true, "", new()))
        ];
    }
    public void Save(string name, NarrationOptions options)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) throw new ArgumentException("Use a preset name of 1–60 characters without control characters.");
        options.Validate();
        var index = settings.NarrationPresets!.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var preset = new NarrationPreset(name, options);
        if (index >= 0) settings.NarrationPresets[index] = preset;
        else
        {
            if (Items.Count >= 30) throw new ArgumentException("Keep at most 30 presets. Remove an unused preset before adding another.");
            settings.NarrationPresets.Add(preset);
        }
    }
    public void Remove(NarrationPreset preset) => settings.NarrationPresets!.Remove(preset);
}
