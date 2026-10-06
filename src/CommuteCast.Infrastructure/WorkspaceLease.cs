namespace CommuteCast.Infrastructure;

/// <summary>A crash-released, thread-independent lease shared by the desktop and maintenance host.</summary>
public sealed class WorkspaceLease : IDisposable
{
    private FileStream? stream;
    public Workspace Workspace { get; }
    private WorkspaceLease(Workspace workspace, FileStream stream) { Workspace = workspace; this.stream = stream; }
    public static WorkspaceLease Acquire(Workspace workspace)
    {
        Workspace.RejectReparsePoints(workspace.Root);
        var path = Path.Combine(workspace.Root, "instance.lease");
        Workspace.RejectFileReparsePoint(path);
        try { return new(workspace, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough)); }
        catch (IOException error) { throw new IOException("CommuteCast or a maintenance operation already holds this workspace. Close it before retrying.", error); }
    }
    public void EnsureHeld()
    {
        if (stream is null) throw new ObjectDisposedException(nameof(WorkspaceLease));
        Workspace.RejectReparsePoints(Workspace.Root);
    }
    public void Dispose() => Interlocked.Exchange(ref stream, null)?.Dispose();
}
