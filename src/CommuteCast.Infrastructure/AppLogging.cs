using System.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace CommuteCast.Infrastructure;

/// <summary>Local operational logs. Never pass scripts, titles, arguments or raw tool output.</summary>
public static class AppLogging
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast", "logs");
    public static Logger CreateLogger(string directory, string component, LogEventLevel minimumLevel = LogEventLevel.Information,
        long fileSizeLimitBytes = 5 * 1024 * 1024, int retainedFileCountLimit = 14)
    {
        if (component is not ("desktop" or "launcher" or "maintenance" or "test")) throw new ArgumentException("Unknown logging component.");
        Workspace.RejectReparsePoints(directory);
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, component + "-*.jsonl")) Workspace.RejectFileReparsePoint(file);
        return new LoggerConfiguration().MinimumLevel.Is(minimumLevel).Enrich.FromLogContext()
            .Enrich.WithProperty("Component", component).Enrich.WithProperty("SessionId", Guid.NewGuid().ToString("N"))
            .Enrich.WithProperty("ProcessId", Environment.ProcessId).Enrich.WithProperty("AppVersion", typeof(AppLogging).Assembly.GetName().Version?.ToString())
            .WriteTo.File(new JsonFormatter(renderMessage: true), Path.Combine(directory, component + "-.jsonl"),
                rollingInterval: RollingInterval.Day, fileSizeLimitBytes: fileSizeLimitBytes, rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFileCountLimit, buffered: false, shared: true).CreateLogger();
    }

    public static void Start(string component, string? directory = null)
    {
        try
        {
            var configured = Environment.GetEnvironmentVariable("COMMUTECAST_LOG_LEVEL");
            var level = Enum.TryParse<LogEventLevel>(configured, true, out var parsed) && Enum.IsDefined(parsed) ? parsed : LogEventLevel.Information;
            Log.Logger = CreateLogger(directory ?? DefaultDirectory, component, level);
            Log.Information("Application started with runtime {Runtime} on OS {OSVersion}", Environment.Version.ToString(), Environment.OSVersion.Version.ToString());
        }
        catch (Exception error)
        {
            // Logging must never prevent startup or damage a successful operation.
            Trace.WriteLine($"CommuteCast logging unavailable ({error.GetType().Name}).");
        }
    }

    public static void Stop() { Log.Information("Application exiting"); Log.CloseAndFlush(); }

    public static void Failure(string operation, Exception error, LogEventLevel level = LogEventLevel.Error) =>
        WriteFailure(Log.Logger, operation, error, level);

    public static void WriteFailure(ILogger logger, string operation, Exception error, LogEventLevel level = LogEventLevel.Error)
    {
        if (!logger.IsEnabled(level)) return;
        // Exception messages/Data may contain source text, filenames or raw subprocess output.
        // Preserve types, codes, method names and line numbers without private message/path data.
        logger.Write(level, "Operation {Operation} failed: {@Failure}", operation, Describe(error, 0));
    }

    private static object Describe(Exception error, int depth) => new
    {
        Type = error.GetType().FullName, error.HResult,
        StatusCode = error is HttpRequestException request ? (int?)request.StatusCode : null,
        SqliteErrorCode = error is Microsoft.Data.Sqlite.SqliteException sqlite ? (int?)sqlite.SqliteErrorCode : null,
        SqliteExtendedErrorCode = error is Microsoft.Data.Sqlite.SqliteException sqliteExtended ? (int?)sqliteExtended.SqliteExtendedErrorCode : null,
        NativeErrorCode = error is System.ComponentModel.Win32Exception native ? (int?)native.NativeErrorCode : null,
        Frames = new StackTrace(error, true).GetFrames()?.Take(40).Select(f => new
        {
            Method = f.GetMethod()?.DeclaringType?.FullName + "." + f.GetMethod()?.Name,
            Line = f.GetFileLineNumber()
        }).ToArray(),
        Inner = depth < 5 && error.InnerException is not null ? Describe(error.InnerException, depth + 1) : null
    };
}
