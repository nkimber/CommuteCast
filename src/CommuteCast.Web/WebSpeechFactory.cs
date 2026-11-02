using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Web;

public interface IWebSpeechFactory
{
    ISpeechService Create(Workspace workspace, AppSettings settings, ISpeechSecrets secrets);
}
public sealed class WebSpeechFactory : IWebSpeechFactory
{
    public ISpeechService Create(Workspace workspace, AppSettings settings, ISpeechSecrets secrets) => new SpeechProviderService(workspace, settings, secrets: secrets);
}
