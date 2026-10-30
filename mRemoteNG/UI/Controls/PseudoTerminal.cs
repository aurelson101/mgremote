// MGRemote modification, 2026-10-30. GPL-2.0; see COPYING.txt.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using mRemoteNG.Connection;

namespace mRemoteNG.UI.Controls;

// Windows ConPTY: independent input/output workers, bounded input and acknowledged output.
internal sealed class PseudoTerminal : IDisposable
{
    private IntPtr _pty;
    private FileStream _input, _output;
    private Process _process;
    private SafeFileHandle _job;
    private readonly Channel<string> _keys = Channel.CreateBounded<string>(128);
    private int _stopped;
    private Task _reader = Task.CompletedTask, _writer = Task.CompletedTask;
    public int ProcessId => _process?.Id ?? 0;
    public bool IsRunning => _stopped == 0 && _process != null && !_process.HasExited;
    public Task ShutdownCompletion { get; private set; } = Task.CompletedTask;

    public void Start(string executable, string arguments, Func<string, Task> display)
    {
        if (_process != null || _stopped != 0) throw new InvalidOperationException("Session déjà démarrée.");
        SafeFileHandle read = null, write = null, outputRead = null, inputWrite = null;
        IntPtr attributes = IntPtr.Zero;
        bool initialized = false;
        ProcessInformation info = default;
        try {
            if (!CreatePipe(out read, out inputWrite, IntPtr.Zero, 0) ||
                !CreatePipe(out outputRead, out write, IntPtr.Zero, 0)) throw new Win32Exception();
            Marshal.ThrowExceptionForHR(CreatePseudoConsole(new Coord(120, 30), read, write, 0, out _pty));
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            initialized = true;
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x20016, _pty, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
            // STARTF_USESTDHANDLES with empty handles prevents inheriting the launcher's
            // redirected streams; Windows OpenSSH initializes its handles on this ConPTY.
            var startup = new StartupInfoEx { Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100 }, Attributes = attributes };
            // Explicit executable, no cmd.exe/shell; existing validated OpenSSH arguments remain intact.
            if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\" " + arguments), IntPtr.Zero, IntPtr.Zero,
                false, 0x80000 | 4, IntPtr.Zero, null, ref startup, out info)) throw new Win32Exception();
            _process = Process.GetProcessById(info.ProcessId);
            _job = OpenSshTunnel.ProtectProcessLifetime(_process);
            _input = new FileStream(inputWrite, FileAccess.Write, 4096, false); inputWrite = null;
            _output = new FileStream(outputRead, FileAccess.Read, 4096, false); outputRead = null;
            _reader = Task.Run(async () => {
                var bytes = new byte[4096];
                var buffer = new char[4096];
                var decoder = new UTF8Encoding(false).GetDecoder();
                try {
                    int count;
                    while ((count = _output.Read(bytes, 0, bytes.Length)) > 0) {
                        int chars = decoder.GetChars(bytes, 0, count, buffer, 0, false);
                        if (_stopped == 0 && chars > 0) await display(new string(buffer, 0, chars)).ConfigureAwait(false);
                    }
                } catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
            });
            _writer = Task.Run(async () => {
                try {
                    await foreach (string value in _keys.Reader.ReadAllAsync()) {
                        if (_stopped != 0) break;
                        byte[] bytes = Encoding.UTF8.GetBytes(value);
                        _input.Write(bytes, 0, bytes.Length); _input.Flush();
                    }
                } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            });
            if (ResumeThread(info.Thread) == uint.MaxValue) throw new Win32Exception();
        } catch { Dispose(); throw; }
        finally {
            read?.Dispose(); write?.Dispose(); inputWrite?.Dispose(); outputRead?.Dispose();
            if (info.Thread != IntPtr.Zero) CloseHandle(info.Thread);
            if (info.Process != IntPtr.Zero) CloseHandle(info.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
        }
    }
    public bool Send(string text) => _stopped == 0 && text.Length <= 65536 && _keys.Writer.TryWrite(text);
    public void Resize(int columns, int rows)
    {
        if (_stopped == 0 && _pty != IntPtr.Zero)
            Marshal.ThrowExceptionForHR(ResizePseudoConsole(_pty, new Coord((short)Math.Clamp(columns, 2, 500), (short)Math.Clamp(rows, 2, 300))));
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _keys.Writer.TryComplete();
        _job?.Dispose(); // Kernel terminates the owned process tree even if the application exits.
        IntPtr pty = _pty; _pty = IntPtr.Zero;
        ShutdownCompletion = Task.Run(async () => {
            // Keep draining output while ClosePseudoConsole emits its final frame.
            if (pty != IntPtr.Zero) ClosePseudoConsole(pty);
            await Task.WhenAll(_reader, _writer).ConfigureAwait(false);
            _input?.Dispose(); _output?.Dispose(); _process?.Dispose();
        });
    }
    [StructLayout(LayoutKind.Sequential)] private readonly struct Coord(short x, short y) { public readonly short X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo {
        public int Size; public string Reserved, Desktop, Title; public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedSize; public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr security, int size);
    [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr handle);
    [DllImport("kernel32.dll")] private static extern int ResizePseudoConsole(IntPtr handle, Coord size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
