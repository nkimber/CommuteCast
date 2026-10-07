using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CommuteCast.Infrastructure;

public static partial class ProcessRunner
{
    // stdout is a duplicate of the already held regular file, so fd: remains
    // seekable without reopening a pathname or releasing its exclusive lock.
    public static async Task<ProcessResult> RunToFileAsync(string executable, IEnumerable<string> arguments,
        FileStream output, long maximumBytes, TimeSpan timeout, CancellationToken ct = default, Action<int>? processStarted = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Held audio output requires Windows.");
        if (!output.CanSeek || !output.CanWrite || maximumBytes <= 0 || timeout <= TimeSpan.Zero) throw new ArgumentException("A writable regular file and positive output/time limits are required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(timeout);
        deadline.Token.ThrowIfCancellationRequested(); await output.FlushAsync(deadline.Token);
        var path = ResolveFileExecutable(executable);
        var command = new StringBuilder(string.Join(" ", new[] { path }.Concat(arguments).Select(QuoteFileProcessArgument)));
        if (command.Length >= 32767) throw new IOException("The audio command exceeds the Windows command-line limit.");
        IntPtr job = IntPtr.Zero, stdout = IntPtr.Zero, stdin = IntPtr.Zero, errorRead = IntPtr.Zero, errorWrite = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero; var initialized = false;
        var information = new FileProcessInformation(); Process? child = null; Task<string>? errors = null;
        StreamReader? reader = null;
        try
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw FileProcessError("Windows could not create an owned audio process job.");
            var limits = new FileJobLimits { Basic = new FileJobBasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<FileJobLimits>())) throw FileProcessError("Windows could not bind audio process lifetime.");
            if (!DuplicateHandle(GetCurrentProcess(), output.SafeFileHandle, GetCurrentProcess(), out stdout, 0, true, 2)) throw FileProcessError("Windows could not inherit the held audio output.");
            var security = new FileProcessSecurity { Size = Marshal.SizeOf<FileProcessSecurity>(), Inherit = 1 };
            stdin = CreateFileInput("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
            if (stdin == new IntPtr(-1)) { stdin = IntPtr.Zero; throw FileProcessError("Windows could not create closed audio input."); }
            if (!CreatePipe(out errorRead, out errorWrite, ref security, 0) || !SetHandleInformation(errorRead, 1, 0)) throw FileProcessError("Windows could not capture audio diagnostics.");
            nuint bytes = 0; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw FileProcessError("Windows could not initialize audio handle inheritance.");
            initialized = true; handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handles, 0, stdin); Marshal.WriteIntPtr(handles, IntPtr.Size, stdout); Marshal.WriteIntPtr(handles, IntPtr.Size * 2, errorWrite);
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20002, handles, (nuint)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero)) throw FileProcessError("Windows could not restrict audio handle inheritance.");
            var startup = new FileProcessStartupEx { Startup = new FileProcessStartup { Size = Marshal.SizeOf<FileProcessStartupEx>(), Flags = 0x100, Input = stdin, Output = stdout, Error = errorWrite }, Attributes = attributes };
            deadline.Token.ThrowIfCancellationRequested();
            if (!CreateFileProcess(path, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080004, IntPtr.Zero, null, ref startup, out information)) throw FileProcessError("Windows could not start the configured audio tool.");
            if (!AssignProcessToJobObject(job, information.Process)) throw FileProcessError("Windows could not contain the owned audio process.");
            child = Process.GetProcessById((int)information.ProcessId);
            CloseFileProcessHandle(ref stdout); CloseFileProcessHandle(ref stdin); CloseFileProcessHandle(ref errorWrite);
            var errorStream = new FileStream(new SafeFileHandle(errorRead, true), FileAccess.Read, 4096, false); errorRead = IntPtr.Zero;
            reader = new StreamReader(errorStream, Encoding.UTF8);
            errors = ReadBoundedAsync(reader, deadline.Token);
            if (ResumeThread(information.Thread) == uint.MaxValue) throw FileProcessError("Windows could not resume the owned audio process.");
            processStarted?.Invoke((int)information.ProcessId);
            var exit = child.WaitForExitAsync(deadline.Token);
            while (!exit.IsCompleted)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (output.Length > maximumBytes) throw new IOException("The audio tool exceeded its output size limit.");
                await Task.WhenAny(exit, Task.Delay(50, deadline.Token));
            }
            await exit; await errors.WaitAsync(deadline.Token);
            if (output.Length > maximumBytes) throw new IOException("The audio tool exceeded its output size limit.");
            return new(child.ExitCode, "", await errors);
        }
        catch
        {
            if (job != IntPtr.Zero) TerminateJobObject(job, 1);
            if (information.Process != IntPtr.Zero) TerminateProcess(information.Process, 1); // also covers failure before assignment/resume
            if (information.Process != IntPtr.Zero && WaitForSingleObject(information.Process, 3000) != 0)
                throw new IOException("The owned audio process did not finish terminating; its output remains reserved.");
            if (child is not null) try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            if (errors is not null) try { await errors.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or ObjectDisposedException) { }
            ct.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("The audio tool exceeded its configured time limit.");
            throw;
        }
        finally
        {
            reader?.Dispose();
            child?.Dispose(); CloseFileProcessHandle(ref information.Thread); CloseFileProcessHandle(ref information.Process); CloseFileProcessHandle(ref job);
            CloseFileProcessHandle(ref stdout); CloseFileProcessHandle(ref stdin); CloseFileProcessHandle(ref errorRead); CloseFileProcessHandle(ref errorWrite);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
        }
    }
    private static string ResolveFileExecutable(string executable)
    {
        var candidates = Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar)
            ? new[] { Path.GetFullPath(executable) }
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(p => Path.Combine(p.Trim('"'), executable)).ToArray();
        foreach (var candidate in candidates)
            foreach (var path in new[] { candidate, candidate + ".exe" })
                if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !path.StartsWith("\\\\", StringComparison.Ordinal)) return Path.GetFullPath(path);
        throw new IOException("The configured local audio executable could not be found. Repair FFmpeg prerequisites and retry.");
    }
    private static string QuoteFileProcessArgument(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Process arguments cannot contain NUL.");
        var quoted = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes); quoted.Append(character); slashes = 0;
        }
        quoted.Append('\\', slashes * 2); return quoted.Append('"').ToString();
    }
    private static IOException FileProcessError(string message) => new(message, new Win32Exception(Marshal.GetLastWin32Error()));
    private static void CloseFileProcessHandle(ref IntPtr handle) { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    [StructLayout(LayoutKind.Sequential)] private struct FileProcessSecurity { public int Size; public IntPtr Descriptor; public int Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct FileProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct FileProcessStartup
    {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public ushort Show, ReservedBytes; public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileProcessStartupEx { public FileProcessStartup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct FileJobBasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcesses;
        public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileJobLimits
    {
        public FileJobBasicLimits Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref FileJobLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr job, uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr source, SafeFileHandle handle, IntPtr target, out IntPtr duplicate, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFileInput(string path, uint access, uint share, ref FileProcessSecurity security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref FileProcessSecurity security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, nuint bytes, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateFileProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string? directory, ref FileProcessStartupEx startup, out FileProcessInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
