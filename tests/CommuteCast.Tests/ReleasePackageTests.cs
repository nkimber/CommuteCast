using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class ReleasePackageTests
{
    [Fact] public async Task InventoryIdentityIsStableAcrossResealingAndChangesWithContent()
    {
        using var test = new TestWorkspace(); var package = Fixture(test);
        var first = await ReleasePackage.SealAsync(package); var second = await ReleasePackage.SealAsync(package);
        Assert.Equal(first.PackageId, second.PackageId); Assert.Equal("win-x64", second.Target); Assert.Equal(SqliteSchema.CurrentVersion, second.MaximumSchema);
        var validated = await ReleasePackage.ValidateAsync(package); Assert.Equal(second.PackageId, validated.PackageId); Assert.Equal(second.Files, validated.Files);
        await File.WriteAllTextAsync(Path.Combine(package, "README.md"), "Changed documented behavior");
        await Assert.ThrowsAsync<IOException>(() => ReleasePackage.ValidateAsync(package));
        Assert.NotEqual(first.PackageId, (await ReleasePackage.SealAsync(package)).PackageId);
    }
    [Theory] [InlineData("changed")] [InlineData("missing")] [InlineData("unlisted")] [InlineData("fingerprint")]
    [InlineData("runtime")] [InlineData("target")] [InlineData("schema")] [InlineData("contract")] [InlineData("traversal")]
    [InlineData("duplicate")] [InlineData("negative-size")]
    public async Task ChangedOrIncompatibleReleaseIsRefused(string defect)
    {
        using var test = new TestWorkspace(); var package = Fixture(test); var manifest = await ReleasePackage.SealAsync(package);
        switch (defect)
        {
            case "changed": await File.WriteAllTextAsync(Path.Combine(package, "README.md"), "Changed"); break;
            case "missing": File.Delete(Path.Combine(package, "app", "CommuteCast.Core.dll")); break;
            case "unlisted": await File.WriteAllTextAsync(Path.Combine(package, "app", "unexpected.json"), "{}"); break;
            case "fingerprint": manifest = manifest with { PackageId = new string('0', 64) }; break;
            case "runtime": manifest = manifest with { BundledRuntime = "Changed declared runtime" }; break;
            case "target": manifest = manifest with { Target = "linux-x64" }; break;
            case "schema": manifest = manifest with { MaximumSchema = 99 }; break;
            case "contract": manifest = manifest with { ProviderContract = 99 }; break;
            case "traversal": manifest = manifest with { Files = manifest.Files.Append(new("app/../escape", 0, new string('0', 64))).ToArray() }; break;
            case "duplicate": manifest = manifest with { Files = manifest.Files.Append(manifest.Files[0]).ToArray() }; break;
            case "negative-size": manifest = manifest with { Files = manifest.Files.Select((f, i) => i == 0 ? f with { Bytes = -1 } : f).ToArray() }; break;
        }
        if (defect is not ("changed" or "missing" or "unlisted")) await File.WriteAllTextAsync(Path.Combine(package, ReleasePackage.ManifestName), JsonSerializer.Serialize(manifest));
        await Assert.ThrowsAsync<IOException>(() => ReleasePackage.ValidateAsync(package));
    }
    [Theory] [InlineData("app/queue.db")] [InlineData("app/queue.db-journal")] [InlineData("app/draft.json")]
    [InlineData("services/speech/provider-lock.local.json")] [InlineData("app/jobs/record/source.txt")]
    [InlineData("services/speech/__pycache__/app.pyc")] [InlineData("app/CON.txt")]
    public async Task PrivateStateOrUnsafeFilesCannotBeSealed(string relative)
    {
        using var test = new TestWorkspace(); var package = Fixture(test); var path = Path.Combine(package, relative.Replace('/', Path.DirectorySeparatorChar));
        // A reserved device path is injected into the manifest, since Windows cannot create it as an ordinary file.
        if (relative.EndsWith("CON.txt"))
        {
            var manifest = await ReleasePackage.SealAsync(package);
            await File.WriteAllTextAsync(Path.Combine(package, ReleasePackage.ManifestName), JsonSerializer.Serialize(manifest with { Files = manifest.Files.Append(new(relative, 0, new string('0', 64))).ToArray() }));
            await Assert.ThrowsAsync<IOException>(() => ReleasePackage.ValidateAsync(package)); return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "Synthetic private artifact");
        await Assert.ThrowsAsync<IOException>(() => ReleasePackage.SealAsync(package));
        Assert.False(File.Exists(Path.Combine(package, ReleasePackage.ManifestName)));
    }
    [Fact] public async Task CancelledSealPreservesExistingManifest()
    {
        using var test = new TestWorkspace(); var package = Fixture(test); await ReleasePackage.SealAsync(package);
        var path = Path.Combine(package, ReleasePackage.ManifestName); var before = await Workspace.HashFileAsync(path);
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReleasePackage.SealAsync(package, cancel.Token));
        Assert.Equal(before, await Workspace.HashFileAsync(path));
    }
    private static string Fixture(TestWorkspace test)
    {
        var root = Path.Combine(test.Workspace.Root, "portable-fixture");
        foreach (var relative in new[] { "README.md", "THIRD-PARTY-NOTICES.md", "app/CommuteCast.Desktop.runtimeconfig.json", "app/CommuteCast.Maintenance.runtimeconfig.json", "services/compose.yaml", "services/speech/requirements.lock.txt", "services/speech/model-checksums.txt" })
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "Synthetic package fixture");
        }
        // Real PE version resources exercise inventory parsing. These fixtures do not establish executable/runtime health.
        foreach (var name in new[] { "CommuteCast.Desktop.exe", "CommuteCast.Maintenance.exe", "CommuteCast.Core.dll", "coreclr.dll" }) File.Copy(typeof(CommuteCast.Core.Job).Assembly.Location, Path.Combine(root, "app", name));
        return root;
    }
}
