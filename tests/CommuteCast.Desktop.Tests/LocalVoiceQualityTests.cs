using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommuteCast.Core;
using CommuteCast.Desktop;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class LocalVoiceQualityTests
{
    private sealed class Errors : TraceListener
    {
        public StringBuilder Text { get; } = new();
        public override void Write(string? message) => Text.Append(message);
        public override void WriteLine(string? message) => Text.AppendLine(message);
    }
    [Fact] public Task SpeakerProfilesAndPronunciationsSurviveLibrarySaveWithoutChangingCapturedCast() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = new MainViewModel(new() { QueuePaused = true }, workspace);
        try
        {
            model.PodcastMode = true; var row = model.Cast[0]; row.Personality = model.SpeakerLibrary[0];
            row.Voice = "af_heart"; row.LocalBlendVoice = "af_bella"; row.LocalBlendWeight = .3; row.LocalGainDb = -2; row.Pronunciation = "API=A P I";
            var captured = model.CaptureEpisode(); await DesktopHost.Execute(model.SavePersonalityCommand, row);
            row.LocalBlendWeight = .6; row.Pronunciation = "API=api";
            Assert.Equal(.3, captured.Speakers[0].LocalVoice!.BlendWeight); Assert.Equal("API=A P I", captured.Speakers[0].Pronunciation);
            row.Personality = model.SpeakerLibrary.First(p => p.Id == row.Personality!.Id);
            Assert.Equal(.3, row.LocalBlendWeight); Assert.Equal(-2, row.LocalGainDb); Assert.Equal("API=A P I", row.Pronunciation);
            var saved = await workspace.LoadSettingsAsync(); Assert.Contains(saved.SpeakerVoiceBindings, b => b.LocalVoice?.BlendVoice == "af_bella" && b.Pronunciation == "API=A P I");
            model.Engine = "piper"; Assert.Null(row.LocalVoice);
        }
        finally { await model.DisposeAsync(); }
    });
    [Theory] [InlineData("kokoro")] [InlineData("piper")]
    public Task LocalVoiceControlsBindAndRenderAtTheMinimumWindowSize(string engine) => DesktopHost.Run(async () =>
    {
        var settings = new AppSettings { QueuePaused = true, Engine = engine, Voice = engine == "kokoro" ? "af_heart" : "en_US-lessac-medium" };
        settings.Providers[engine] = new(engine, "fixture", engine == "kokoro" ? ["af_heart", "af_bella"] : ["en_US-lessac-medium"], "ready", 0, LocalVoiceContract: 1);
        var model = new MainViewModel(settings, DesktopHost.Workspace()); var errors = new Errors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors); var previous = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            model.NavigateCommand.Execute("compose"); var window = new MainWindow(model); DesktopHost.Layout(window, 1060, 700); SaveImage(window, "local-voice-" + engine + ".png");
            foreach (var expander in Children((DependencyObject)window.Content).OfType<Expander>().Where(e => e.Header?.ToString() == "Local voice delivery")) expander.IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window, 1060, 700);
            Assert.Contains(Children((DependencyObject)window.Content).OfType<Slider>(), s => System.Windows.Automation.AutomationProperties.GetName(s) == "Local voice level adjustment");
            SaveImage(window, "local-voice-" + engine + ".png");
            ScrollTo(Children((DependencyObject)window.Content).OfType<Expander>().Single(e => e.Header?.ToString() == "Local voice delivery"));
            SaveImage(window, "local-delivery-" + engine + ".png");
            Assert.False(model.TunePiperVariation);
            model.PodcastMode = true; model.Cast[0].Voice = settings.Voice; model.Cast[1].Voice = settings.Voice;
            var cast = new PodcastCastWindow { DataContext = model }; DesktopHost.Layout(cast, 750, 600); SaveImage(cast, "local-cast-" + engine + ".png");
            foreach (var e in Children((DependencyObject)cast.Content).OfType<Expander>().Where(e => e.Header?.ToString() == "Local voice profile")) e.IsExpanded = true;
            await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(cast, 750, 600); SaveImage(cast, "local-cast-" + engine + ".png");
            ScrollTo(Children((DependencyObject)cast.Content).OfType<Expander>().First(e => e.Header?.ToString() == "Local voice profile"));
            SaveImage(cast, "local-speaker-" + engine + ".png");
            Assert.False(model.Cast[0].TuneLocalVariation);
            Assert.Equal("", errors.Text.ToString());
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); PresentationTraceSources.DataBindingSource.Switch.Level = previous; await model.DisposeAsync(); }
    });
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Children(child)) yield return nested; }
    }
    private static void ScrollTo(FrameworkElement target)
    {
        DependencyObject? parent = target;
        while ((parent = VisualTreeHelper.GetParent(parent)) is not null)
            if (parent is ScrollViewer scroll && scroll.Content is Visual content)
            { scroll.ScrollToVerticalOffset(target.TransformToAncestor(content).Transform(new()).Y); scroll.UpdateLayout(); return; }
        throw new InvalidOperationException("Delivery controls need an accessible scroll container.");
    }
    private static void SaveImage(Window window, string name)
    {
        var content = (FrameworkElement)window.Content; window.Content = null; content.DataContext = window.DataContext; content.Resources = window.Resources;
        if (content is Panel panel) panel.Background = window.Background;
        content.Measure(new(window.Width, window.Height)); content.Arrange(new(0, 0, window.Width, window.Height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); window.Content = content;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); Directory.CreateDirectory("TestResults"); using var file = File.Create(Path.Combine("TestResults", name)); encoder.Save(file);
    }
}
