using System.IO;
using CommuteCast.Core;

namespace CommuteCast.Desktop;

/// <summary>Offline, copyable guidance; the skill ships with the app rather than depending on a local Codex installation.</summary>
public static class CodexGuideContent
{
    public static string SkillContent { get; } = ReadSkill();

    public static string CreateSkillIntroduction { get; } = """
        $skill-creator Create a personal skill named commute-narrative.
        Make it available across my Codex projects, with automatic discovery enabled.
        Use the SKILL.md content below and validate the completed skill.
        """;

    public static string CreateSkillPrompt => CreateSkillIntroduction + "\n\n" + SkillContent;

    public static string GeneratePrompt { get; } = """
        $commute-narrative Write a 30-minute explanation of [your topic] for a curious beginner.
        Use a warm, engaging tone and concrete examples. Verify current or uncertain facts.
        Aim for about 4,200 to 4,800 words, then privately check the word count and estimated listening time.
        Do not report those checks.
        """ + "\n" + NarrationPrompt.OutputInstructions;

    public static string BillsExample { get; } = """
        $commute-narrative Write a 30-minute narrative about the Buffalo Bills for a curious beginner.
        Explain their history, major players, football style, and fan culture. Verify current details.
        Aim for about 4,200 to 4,800 words and privately check the word count before delivering.
        Do not report that check.
        """ + "\n" + NarrationPrompt.OutputInstructions;

    private static string ReadSkill()
    {
        using var stream = typeof(CodexGuideContent).Assembly.GetManifestResourceStream("CommuteCast.Codex.CommuteNarrative")
            ?? throw new InvalidOperationException("The bundled commute-narrative skill is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().TrimEnd();
    }
}
