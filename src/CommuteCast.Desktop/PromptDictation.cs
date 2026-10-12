using System.Windows.Input;
using System.Windows.Threading;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop;

public sealed partial class MainViewModel
{
    private IDictationService dictationService = new DictationService();
    internal Func<IMicrophoneRecording> MicrophoneFactory { get; set; } = () => new MicrophoneRecording();
    internal IDictationService DictationServiceForTests { set { dictationService.Dispose(); dictationService = value; } }
    private IMicrophoneRecording? microphone;
    private CancellationTokenSource? dictationCancellation;
    private readonly Dispatcher dictationDispatcher = Dispatcher.CurrentDispatcher;
    private string dictationStatus = "Record your listening goal in English (up to two minutes). Text is added to the box for you to edit.";
    private bool dictationBusy, dictationRecording;
    public string DictationStatus { get => dictationStatus; private set => Set(ref dictationStatus, value); }
    public bool CanRecordGoal => !dictationBusy;
    public bool CanStopGoal => dictationRecording;
    public bool CanCancelGoal => dictationBusy;
    public ICommand RecordGoalCommand { get; private set; } = null!;
    public ICommand StopGoalCommand { get; private set; } = null!;
    public ICommand CancelGoalCommand { get; private set; } = null!;
    public ICommand RepairDictationCommand { get; private set; } = null!;
    public ICommand AddGoalTranscriptCommand { get; private set; } = null!;
    private string pendingGoalTranscript = "";
    public string PendingGoalTranscript { get => pendingGoalTranscript; private set { Set(ref pendingGoalTranscript, value); Raise(nameof(HasPendingGoalTranscript)); } }
    public bool HasPendingGoalTranscript => PendingGoalTranscript.Length > 0;

    private void InitializeDictation()
    {
        RecordGoalCommand = Command(_ => StartGoalRecordingAsync(), false);
        StopGoalCommand = Command(_ => FinishGoalRecordingAsync(), false);
        CancelGoalCommand = Command(_ => { CancelGoalRecording(); return Task.CompletedTask; }, false);
        RepairDictationCommand = Command(_ => CheckDictationAsync(), false);
        DiscardGoalTranscriptCommand = Command(_ => { PendingGoalTranscript = ""; return Task.CompletedTask; }, false);
        AddGoalTranscriptCommand = Command(_ =>
        {
            if (!dictationBusy && HasPendingGoalTranscript) AddGoalTranscript(PendingGoalTranscript);
            return Task.CompletedTask;
        }, false);
    }

    private void DictationState(bool busy, bool recording = false)
    {
        dictationBusy = busy; dictationRecording = recording;
        Raise(nameof(CanRecordGoal)); Raise(nameof(CanStopGoal)); Raise(nameof(CanCancelGoal));
    }

    private CancellationTokenSource BeginDictation()
    {
        dictationCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        DictationState(true);
        return dictationCancellation;
    }

    private async Task CheckDictationAsync()
    {
        if (dictationBusy) return;
        using var cancellation = BeginDictation();
        DictationStatus = "Checking local Whisper and starting it if stopped…";
        try
        {
            await dictationService.ReadyAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            DictationStatus = "Local Whisper is ready. Choose Record to speak your listening goal.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) { if (ReferenceEquals(dictationCancellation, cancellation)) DictationStatus = error.Message; }
        finally { EndDictation(cancellation); }
    }

    private async Task StartGoalRecordingAsync()
    {
        if (dictationBusy) return;
        if (HasPendingGoalTranscript) { DictationStatus = "Add or copy the previous transcript, then choose Discard transcript before recording again."; return; }
        if (PromptGoal.Length >= 2000) { DictationStatus = "The goal already has 2,000 characters. Shorten it before recording."; return; }
        var cancellation = BeginDictation();
        DictationStatus = "Preparing local Whisper…";
        try
        {
            await dictationService.ReadyAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var recording = MicrophoneFactory(); microphone = recording;
            recording.LimitReached += () => _ = dictationDispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(microphone, recording) && dictationRecording)
                    StopGoalCommand.Execute(null);
            });
            recording.Failed += error => _ = dictationDispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(microphone, recording)) return;
                CancelGoalRecording();
                DictationStatus = "Microphone recording failed. Check the device and Windows microphone access, then try again.";
            });
            recording.Start(); DictationState(true, true);
            DictationStatus = "Recording… Choose Stop and transcribe when finished, or Cancel to discard. Stops automatically after two minutes.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { EndDictation(cancellation); cancellation.Dispose(); }
        catch (Exception error)
        {
            if (ReferenceEquals(dictationCancellation, cancellation)) DictationStatus = "Could not start recording. " + error.Message;
            EndDictation(cancellation); cancellation.Dispose();
        }
    }

    private async Task FinishGoalRecordingAsync()
    {
        if (!dictationRecording || microphone is null || dictationCancellation is null) return;
        var recording = microphone;
        using var cancellation = dictationCancellation;
        DictationState(true); DictationStatus = "Transcribing locally…";
        try
        {
            var wav = await recording.StopAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            recording.Dispose(); microphone = null;
            var text = await dictationService.TranscribeAsync(wav, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (ReferenceEquals(dictationCancellation, cancellation)) AddGoalTranscript(text);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (ReferenceEquals(dictationCancellation, cancellation)) DictationStatus = "Could not transcribe the recording. " + error.Message;
        }
        finally { EndDictation(cancellation); }
    }

    internal static string AppendGoalTranscript(string goal, string transcript) =>
        string.IsNullOrWhiteSpace(goal) ? transcript.Trim() : goal.TrimEnd() + Environment.NewLine + transcript.Trim();

    private void AddGoalTranscript(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { DictationStatus = "No speech was detected. Try again closer to your microphone."; return; }
        var combined = AppendGoalTranscript(PromptGoal, text);
        if (combined.Length > 2000)
        {
            PendingGoalTranscript = text;
            DictationStatus = "This would exceed 2,000 characters. Shorten your goal, then choose Add recorded text, or copy the transcript below before discarding it.";
            return;
        }
        PromptGoal = combined; PendingGoalTranscript = "";
        DictationStatus = "Recorded text added. Review it before building your prompt.";
    }

    public ICommand DiscardGoalTranscriptCommand { get; private set; } = null!;
    private void EndDictation(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(dictationCancellation, cancellation)) return;
        microphone?.Dispose(); microphone = null;
        dictationCancellation = null; DictationState(false);
    }

    private void CancelGoalRecording()
    {
        var cancellation = dictationCancellation;
        if (cancellation is null) return;
        cancellation.Cancel();
        var wasRecording = dictationRecording;
        EndDictation(cancellation);
        if (wasRecording) cancellation.Dispose();
        DictationStatus = "Recording cancelled. Your listening goal is unchanged.";
    }
}
