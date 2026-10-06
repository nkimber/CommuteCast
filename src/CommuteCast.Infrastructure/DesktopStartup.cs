namespace CommuteCast.Infrastructure;

public enum DesktopMode { Editor, Maintenance, Setup }
public record DesktopStartup(DesktopMode Mode, string? InstallRoot = null, string? PrivateRoot = null)
{
    public static DesktopStartup Parse(IReadOnlyList<string> args, bool setupHost = false)
    {
        var mode = setupHost ? DesktopMode.Setup : DesktopMode.Editor; var offset = 0;
        if (args.Count > 0 && args[0] is "--setup" or "--maintenance") { mode = args[0] == "--setup" ? DesktopMode.Setup : DesktopMode.Maintenance; offset = 1; }
        if (mode != DesktopMode.Setup)
        {
            if (args.Count != offset) throw new ArgumentException("Launch the editor without arguments, or use --maintenance or --setup.");
            return new(mode);
        }
        string? install = null, root = null;
        for (var i = offset; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--install-root" when install is null && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal): install = Path.GetFullPath(args[++i]); break;
                case "--root" when root is null && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal): root = Path.GetFullPath(args[++i]); break;
                default: throw new ArgumentException("Setup accepts each of --install-root and --root once, with a separate local folder. --root is an explicit private-workspace override.");
            }
        }
        return new(mode, install, root);
    }
}
