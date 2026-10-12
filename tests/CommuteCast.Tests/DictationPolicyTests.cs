using System.Text.Json;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class DictationPolicyTests
{
    private static string Container(string ip = "127.0.0.1", string image = "sha256:approved", string owner = "CommuteCast", string port = "8767") => JsonSerializer.Serialize(new
    {
        Image = image,
        Config = new { Labels = new Dictionary<string, string>
        {
            ["com.commutecast.owner"] = owner, ["com.commutecast.contract"] = "1",
            ["com.docker.compose.project"] = "commutecast", ["com.docker.compose.service"] = "whisper"
        } },
        HostConfig = new { PortBindings = new Dictionary<string, object> { ["8767/tcp"] = new[] { new { HostIp = ip, HostPort = port } } } }
    });

    [Fact]
    public void OwnedPinnedLocalContainerIsAccepted()
    {
        using var document = JsonDocument.Parse(Container());
        DictationService.ValidateContainer(document.RootElement, "sha256:approved");
    }

    [Theory]
    [InlineData("0.0.0.0", "sha256:approved", "CommuteCast", "8767")]
    [InlineData("127.0.0.1", "sha256:other", "CommuteCast", "8767")]
    [InlineData("127.0.0.1", "sha256:approved", "AnotherApp", "8767")]
    [InlineData("127.0.0.1", "sha256:approved", "CommuteCast", "9999")]
    public void ChangedOwnershipImageOrPortIsRejected(string ip, string image, string owner, string port)
    {
        using var document = JsonDocument.Parse(Container(ip, image, owner, port));
        Assert.Throws<IOException>(() => DictationService.ValidateContainer(document.RootElement, "sha256:approved"));
    }
}
