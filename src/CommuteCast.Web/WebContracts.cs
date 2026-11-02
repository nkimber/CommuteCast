using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Web;

public sealed record WebSettings(M365Destination Destination, bool AutoPublish = true, string FolderName = "", string FolderUrl = "")
{
    public static WebSettings Empty => new(new M365Destination());
}
public sealed record PublishingRecord(string JobId, string Status, M365Destination Destination, string FolderUrl,
    string Error = "", M365Upload? Receipt = null, DateTimeOffset? PublishedUtc = null);
public sealed record ComposeRequest(string Title, string Source, NarrationOptions Options, string? GenerationPrompt = null, PodcastEpisode? Episode = null, string? SubmissionId = null);
public sealed record DraftRequest(Draft Draft, string Revision, NarrationOptions? Options = null);
public sealed record EditorDraft(Draft Draft, NarrationOptions? Options = null);
public sealed record DestinationRequest(M365Destination Destination, bool AutoPublish, string? Secret = null);
public sealed record ProviderRequest(string Engine, string? Model = null, string? ApiKey = null);
public sealed record QueueRequest(bool Paused);
public sealed record PromptRequest(NarrationBrief Brief);
public sealed record PublicationRequest(bool UseCurrentDestination = false);
