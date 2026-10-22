using System.Collections;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using Vigo.Core.Services;

namespace Vigo.Git.Terminal;

/// <summary>
/// A shell behind a Windows pseudo console (ConPTY, ADR-071). The shell and everything it starts live in a job object
/// that kills them when the session is disposed (or VIGO dies): closing a terminal never leaves a process behind.
/// Output is read on a dedicated thread into a bounded channel (the pipe is drained even when nobody reads any more,
/// which <c>ClosePseudoConsole</c> needs on older Windows); input is written from a queue, so <see cref="Write"/>
/// never blocks the UI thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtySession : ITerminalSession
{
    private const int OutputChunkSize = 16 * 1024;
    private const int OutputChunksInFlight = 32;

    private readonly nint _console;
    private readonly nint _process;
    private readonly nint _job;
    private readonly FileStream _outputPipe;
    private readonly FileStream _inputPipe;
    private readonly Channel<byte[]> _output = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(OutputChunksInFlight) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<byte[]> _input = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposed = new();
    private readonly ManualResetEvent _processWait;
    private readonly RegisteredWaitHandle _registration;
    private readonly Thread _pump;
    private readonly Lock _consoleLock = new();
    private bool _consoleClosed;
    private bool _processClosed;
    private int _disposeStarted;

    private ConPtySession(nint console, nint process, nint job, FileStream outputPipe, FileStream inputPipe)
    {
        _console = console;
        _process = process;
        _job = job;
        _outputPipe = outputPipe;
        _inputPipe = inputPipe;
        _pump = new Thread(PumpOutput) { IsBackground = true, Name = "VIGO terminal output" };
        _pump.Start();
        _ = Task.Run(PumpInputAsync);
        // The wait handle does not own the process handle: it is closed with the session.
        _processWait = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false) };
        _registration = ThreadPool.RegisterWaitForSingleObject(_processWait, (_, _) => OnProcessExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public ChannelReader<byte[]> Output => _output.Reader;

    public Task<int> Exited => _exited.Task;

    /// <summary>Starts <paramref name="commandLine"/> in <paramref name="workingDirectory"/> behind a new pseudo console.</summary>
    /// <exception cref="IOException">The pseudo console or the process could not be created.</exception>
    public static ConPtySession Start(string commandLine, string workingDirectory, int columns, int rows)
    {
        nint inputRead = 0, inputWrite = 0, outputRead = 0, outputWrite = 0, console = 0, attributes = 0, command = 0, environment = 0, job = 0;
        ConPtyNative.ProcessInformation process = default;
        try
        {
            if (!ConPtyNative.CreatePipe(out inputRead, out inputWrite, 0, 0) || !ConPtyNative.CreatePipe(out outputRead, out outputWrite, 0, 0))
                throw Failed("The pipes of the terminal could not be created");

            var hr = ConPtyNative.CreatePseudoConsole(Size(columns, rows), inputRead, outputWrite, 0, out console);
            if (hr != 0) throw new IOException($"The pseudo console could not be created (HRESULT 0x{hr:X8})");
            // The pseudo console holds its own references to the pty side of the pipes.
            ConPtyNative.CloseHandle(inputRead);
            inputRead = 0;
            ConPtyNative.CloseHandle(outputWrite);
            outputWrite = 0;

            nint size = 0;
            // The first call only reports the size (it fails with ERROR_INSUFFICIENT_BUFFER by design).
            ConPtyNative.InitializeProcThreadAttributeList(0, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!ConPtyNative.InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !ConPtyNative.UpdateProcThreadAttribute(attributes, 0, ConPtyNative.ProcThreadAttributePseudoConsole, console, nint.Size, 0, 0))
                throw Failed("The pseudo console could not be attached to the process");

            // No standard handle is passed on: without this flag a parent whose own output is redirected (a test host, a pipe) leaks it to the shell.
            var startup = new ConPtyNative.StartupInfoEx { AttributeList = attributes };
            startup.StartupInfo.Flags = ConPtyNative.UseStdHandles;
            startup.StartupInfo.Size = (uint)Unsafe.SizeOf<ConPtyNative.StartupInfoEx>();
            command = Marshal.StringToHGlobalUni(commandLine);
            environment = BuildEnvironment();
            if (!ConPtyNative.CreateProcess(null, command, 0, 0, false,
                    ConPtyNative.ExtendedStartupInfoPresent | ConPtyNative.CreateUnicodeEnvironment, environment, workingDirectory, ref startup, out process))
                throw Failed($"The shell could not be started ({commandLine})");
            ConPtyNative.CloseHandle(process.Thread);

            job = CreateKillOnCloseJob(process.Process);

            var output = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, bufferSize: 1, isAsync: false);
            outputRead = 0;
            var input = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, bufferSize: 1, isAsync: false);
            inputWrite = 0;
            var session = new ConPtySession(console, process.Process, job, output, input);
            console = 0;
            process.Process = 0;
            job = 0;
            return session;
        }
        finally
        {
            if (attributes != 0)
            {
                ConPtyNative.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
            if (command != 0) Marshal.FreeHGlobal(command);
            if (environment != 0) Marshal.FreeHGlobal(environment);
            // Only reached with something left when the start failed.
            if (process.Process != 0)
            {
                ConPtyNative.TerminateProcess(process.Process, 1);
                ConPtyNative.CloseHandle(process.Process);
            }
            if (job != 0) ConPtyNative.CloseHandle(job);
            if (console != 0) ConPtyNative.ClosePseudoConsole(console);
            foreach (var handle in (ReadOnlySpan<nint>)[inputRead, inputWrite, outputRead, outputWrite])
            {
                if (handle != 0) ConPtyNative.CloseHandle(handle);
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || Volatile.Read(ref _disposeStarted) != 0) return;
        _input.Writer.TryWrite(data.ToArray());
    }

    public void Resize(int columns, int rows)
    {
        lock (_consoleLock)
        {
            if (_consoleClosed) return;
            _ = ConPtyNative.ResizePseudoConsole(_console, Size(columns, rows));
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _disposed.Cancel();
        _input.Writer.TryComplete();
        if (!_exited.Task.IsCompleted) ConPtyNative.TerminateProcess(_process, 1);
        // ClosePseudoConsole can wait for the output to be drained on older Windows (the pump keeps draining): off the caller's thread.
        _ = Task.Run(Cleanup);
    }

    private void Cleanup()
    {
        CloseConsole();
        // The pump ends when the console is gone (broken pipe); the handles are released after it.
        var pumpEnded = _pump.Join(TimeSpan.FromSeconds(5));
        _registration.Unregister(null);
        _processWait.Dispose();
        // Under the lock: a late exit callback reads the exit code before the handle goes, or not at all.
        lock (_consoleLock)
        {
            _processClosed = true;
            ConPtyNative.CloseHandle(_process);
        }
        // Closing the job kills whatever the shell started and left running.
        ConPtyNative.CloseHandle(_job);
        _inputPipe.Dispose();
        // A pump that outlived the join keeps a usable token: the source is only disposed once the pump is done.
        if (pumpEnded) _disposed.Dispose();
    }

    private void OnProcessExited()
    {
        uint code = 0;
        lock (_consoleLock)
        {
            if (!_processClosed) ConPtyNative.GetExitCodeProcess(_process, out code);
        }
        // The shell is gone, its console stays open until closed: closing it flushes the last output and ends the pump.
        CloseConsole();
        _exited.TrySetResult((int)code);
    }

    private void CloseConsole()
    {
        lock (_consoleLock)
        {
            if (_consoleClosed) return;
            _consoleClosed = true;
        }
        ConPtyNative.ClosePseudoConsole(_console);
    }

    private void PumpOutput()
    {
        var buffer = new byte[OutputChunkSize];
        try
        {
            while (true)
            {
                int read;
                try
                {
                    read = _outputPipe.Read(buffer);
                }
                catch (IOException)
                {
                    // Broken pipe: the console is closed.
                    break;
                }
                if (read == 0) break;
                if (_disposed.IsCancellationRequested) continue;
                try
                {
                    _output.Writer.WriteAsync(buffer.AsSpan(0, read).ToArray(), _disposed.Token).AsTask().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Disposed while the reader was behind: keep draining.
                }
            }
        }
        finally
        {
            _output.Writer.TryComplete();
            _outputPipe.Dispose();
        }
    }

    private async Task PumpInputAsync()
    {
        try
        {
            await foreach (var chunk in _input.Reader.ReadAllAsync())
            {
                await _inputPipe.WriteAsync(chunk);
                await _inputPipe.FlushAsync();
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The console is gone: nothing left to type into.
        }
    }

    private static ConPtyNative.Coord Size(int columns, int rows) =>
        new((short)Math.Clamp(columns, 1, short.MaxValue), (short)Math.Clamp(rows, 1, short.MaxValue));

    private static IOException Failed(string message)
    {
        var error = new Win32Exception(Marshal.GetLastPInvokeError());
        return new IOException($"{message}: {error.Message}", error);
    }

    /// <summary>Environment of VIGO plus the variables that tell the shell it talks to a color terminal.</summary>
    private static nint BuildEnvironment()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name) variables[name] = entry.Value as string ?? string.Empty;
        }
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        variables["TERM_PROGRAM"] = "VIGO";

        var block = new StringBuilder();
        foreach (var (name, value) in variables) block.Append(name).Append('=').Append(value).Append('\0');
        block.Append('\0');
        return Marshal.StringToHGlobalUni(block.ToString());
    }

    /// <summary>Job whose processes die with its last handle; 0 when it could not be set up (the shell then runs without it).</summary>
    private static nint CreateKillOnCloseJob(nint process)
    {
        var job = ConPtyNative.CreateJobObject(0, null);
        if (job == 0) return 0;
        var information = Marshal.AllocHGlobal(ConPtyNative.JobExtendedLimitInformationSize);
        try
        {
            for (var offset = 0; offset < ConPtyNative.JobExtendedLimitInformationSize; offset++) Marshal.WriteByte(information, offset, 0);
            Marshal.WriteInt32(information, 16, (int)ConPtyNative.JobObjectLimitKillOnJobClose);
            if (ConPtyNative.SetInformationJobObject(job, ConPtyNative.JobObjectExtendedLimitInformation, information, ConPtyNative.JobExtendedLimitInformationSize)
                && ConPtyNative.AssignProcessToJobObject(job, process))
                return job;
        }
        finally
        {
            Marshal.FreeHGlobal(information);
        }
        ConPtyNative.CloseHandle(job);
        return 0;
    }
}

/// <summary>Starts <see cref="ConPtySession"/>s.</summary>
[SupportedOSPlatform("windows")]
public sealed class ConPtySessionFactory : ITerminalSessionFactory
{
    public ITerminalSession Start(string commandLine, string workingDirectory, int columns, int rows) =>
        ConPtySession.Start(commandLine, workingDirectory, columns, rows);
}
