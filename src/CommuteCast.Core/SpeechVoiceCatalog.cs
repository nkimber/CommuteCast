using System.Globalization;

namespace CommuteCast.Core;

public sealed record SpeechVoiceChoice(string Id, string DisplayName, bool Available);

public static class SpeechVoiceCatalog
{
    private static readonly IReadOnlyDictionary<string, string> PiperNames = new Dictionary<string, string>
    {
        ["en_US-amy-medium"] = "Amy", ["en_US-bryce-medium"] = "Bryce", ["en_US-joe-medium"] = "Joe",
        ["en_US-lessac-medium"] = "Lessac", ["en_US-ljspeech-medium"] = "LJ Speech",
        ["en_GB-alan-medium"] = "Alan", ["en_GB-alba-medium"] = "Alba", ["en_GB-jenny_dioco-medium"] = "Jenny"
    };

    public static string DefaultVoice(string engine) => engine switch
    { "kokoro" => "af_heart", "piper" => "en_US-lessac-medium", _ => SpeechProviders.Get(engine).DefaultVoice };

    public static SpeechVoiceChoice Describe(string engine, string id, bool available = true)
    {
        var name = id;
        if (engine == "kokoro" && id.Length > 3 && id[2] == '_' && id[..2] is "af" or "am" or "bf" or "bm")
        {
            var accent = id[0] == 'a' ? "US English" : "British English";
            var gender = id[1] == 'f' ? "female" : "male";
            name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(id[3..].Replace('_', ' ')) + $" · {accent} · {gender}";
        }
        else if (engine == "piper" && PiperNames.TryGetValue(id, out var friendly))
            name = friendly + (id.StartsWith("en_GB-", StringComparison.Ordinal) ? " · British English" : " · US English");
        return new(id, name + (available ? "" : " · refresh to verify"), available);
    }

    public static IReadOnlyList<SpeechVoiceChoice> Choices(string engine, IEnumerable<string> installed, string selected)
    {
        var choices = installed.Distinct(StringComparer.Ordinal).Select(id => Describe(engine, id))
            .OrderBy(v => v.DisplayName, StringComparer.Ordinal).ToList();
        if (!choices.Any(v => v.Id == selected)) choices.Insert(0, Describe(engine, selected, false));
        return choices;
    }
}
