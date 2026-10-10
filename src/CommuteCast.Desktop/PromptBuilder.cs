using System.Globalization;
using System.Windows;
using System.Windows.Input;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

public sealed partial class MainViewModel
{
    private NarrationBrief promptBrief = new();
    private NarrationBrief? promptGeneratedFor;
    private string promptText = "";
    private bool promptDraftTouched;

    public bool IsPromptBuilder => page == "prompt";
    public ICommand PrimaryActionCommand => IsPromptBuilder ? BuildPromptCommand : QueueCommand;
    public string PromptTopic { get => promptBrief.Topic; set => UpdateBrief(promptBrief with { Topic = value }, nameof(PromptTopic)); }
    public string PromptGoal { get => promptBrief.Goal; set => UpdateBrief(promptBrief with { Goal = value }, nameof(PromptGoal)); }
    public string PromptAudience { get => promptBrief.Audience; set => UpdateBrief(promptBrief with { Audience = value }, nameof(PromptAudience)); }
    public string PromptMinutes { get => promptBrief.Minutes; set => UpdateBrief(promptBrief with { Minutes = value }, nameof(PromptMinutes)); }
    public NarrativeStyle PromptStyle { get => promptBrief.Style; set => UpdateBrief(promptBrief with { Style = value }, nameof(PromptStyle)); }
    public string PromptTone { get => promptBrief.Tone; set => UpdateBrief(promptBrief with { Tone = value }, nameof(PromptTone)); }
    public string PromptInclude { get => promptBrief.Include; set => UpdateBrief(promptBrief with { Include = value }, nameof(PromptInclude)); }
    public string PromptAvoid { get => promptBrief.Avoid; set => UpdateBrief(promptBrief with { Avoid = value }, nameof(PromptAvoid)); }
    public string PromptSources { get => promptBrief.SourceMaterial; set => UpdateBrief(promptBrief with { SourceMaterial = value }, nameof(PromptSources)); }
    public PromptEvidenceMode PromptEvidence { get => promptBrief.Evidence; set => UpdateBrief(promptBrief with { Evidence = value }, nameof(PromptEvidence)); }
    public string PromptText
    {
        get => promptText;
        set { if (Set(ref promptText, value)) { promptDraftTouched = true; RaisePromptState(); ScheduleDraftSave(); } }
    }
    public PronunciationOption<NarrativeStyle>[] PromptStyles { get; } =
    [new(NarrativeStyle.Explainer, "Engaging explanation"), new(NarrativeStyle.Story, "Story and history"),
        new(NarrativeStyle.PracticalGuide, "Practical guide"), new(NarrativeStyle.BalancedComparison, "Balanced comparison")];
    public PronunciationOption<PromptEvidenceMode>[] PromptEvidenceModes { get; } =
    [new(PromptEvidenceMode.Research, "Research reliable sources"), new(PromptEvidenceMode.SuppliedOnly, "Use supplied material only")];
    public bool PromptIsStale => !string.IsNullOrWhiteSpace(promptText) && promptGeneratedFor != promptBrief;
    public bool CanCopyPrompt => !string.IsNullOrWhiteSpace(promptText) && !PromptIsStale;
    public string PromptStatus => PromptIsStale ? "Your brief has changed. Build the prompt again before copying; rebuilding replaces edits in the prompt."
        : !string.IsNullOrWhiteSpace(promptText) ? "Ready to edit or copy. Paste this prompt into your preferred GAI tool."
        : "Fill in a topic, then build a prompt. A clear listening goal and key questions help give the narration depth.";
    public string PromptLengthEstimate
    {
        get
        {
            try { var minutes = NarrationPrompt.Duration(promptBrief); return $"Aim for {(minutes * 140).ToString("N0", CultureInfo.CurrentCulture)}–{(minutes * 160).ToString("N0", CultureInfo.CurrentCulture)} words. Listening time is an estimate."; }
            catch (ArgumentException) { return "Choose a listening length from 3 to 60 minutes."; }
        }
    }
    public ICommand BuildPromptCommand { get; private set; } = null!;
    public ICommand CopyPromptCommand { get; private set; } = null!;

    private void InitializePromptBuilder()
    {
        BuildPromptCommand = Command(_ =>
        {
            var generated = NarrationPrompt.Build(promptBrief, DateOnly.FromDateTime(DateTime.Today));
            promptGeneratedFor = promptBrief; PromptText = generated; RaisePromptState(); ScheduleDraftSave();
            StatusMessage = "Writing prompt built. Review it, then copy it into your preferred GAI tool.";
            return Task.CompletedTask;
        });
        CopyPromptCommand = Command(_ =>
        {
            Clipboard.SetText(PromptForCopy());
            StatusMessage = "Prompt copied. Ask your GAI tool to write the narration, then paste only its spoken prose into New narration.";
            return Task.CompletedTask;
        }, false);
    }

    private void UpdateBrief(NarrationBrief value, string property)
    {
        if (!Set(ref promptBrief, value, property)) return;
        promptDraftTouched = true; RaisePromptState(); Raise(nameof(PromptLengthEstimate)); ScheduleDraftSave();
    }
    private void RaisePromptState() { Raise(nameof(PromptIsStale)); Raise(nameof(CanCopyPrompt)); Raise(nameof(PromptStatus)); }
    internal string PromptForCopy()
    {
        if (!CanCopyPrompt) throw new ArgumentException("Build a prompt for the current brief before copying it.");
        return PromptText;
    }
    internal Draft CaptureDraft() => new(DraftTitle, Source, promptDraftTouched ? new(promptBrief, promptText, promptGeneratedFor) : null);
    internal void RestorePromptDraft(NarrationPromptDraft? saved)
    {
        if (saved is null) return;
        saved.ValidateStorage(); promptBrief = saved.Brief; promptText = saved.Prompt; promptGeneratedFor = saved.GeneratedFor;
        promptDraftTouched = true; Raise(null);
    }
}
