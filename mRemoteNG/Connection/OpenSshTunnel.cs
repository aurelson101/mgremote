// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using mRemoteNG.Security;

namespace mRemoteNG.Connection;

internal sealed class OpenSshTunnel : IDisposable
{
    private readonly Process _process;
    private readonly SafeFileHandle _job;
    private int _disposed;
    public int LocalPort { get; }
    public int ProcessId { get; }
    private OpenSshTunnel(Process process, SafeFileHandle job, int localPort) { _process = process; _job = job; LocalPort = localPort; ProcessId = process.Id; }

    internal static int ReserveLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try { listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    internal static async Task<OpenSshTunnel> StartAsync(ConnectionInfo bastion, string destination, int port, CancellationToken cancellationToken)
    {
        if (bastion.Protocol != Protocol.ProtocolType.SSH2) throw new ArgumentException("Le bastion doit utiliser SSH version 2.");
        int localPort = ReserveLocalPort();
        var start = WindowsSecurityPolicy.BuildOpenSshStartInfo(bastion.Hostname, bastion.Username, bastion.Port, bastion.SSHOptions);
        start.Arguments = WindowsSecurityPolicy.BuildSshTunnelArguments(bastion.Hostname, bastion.Username, bastion.Port,
            bastion.SSHOptions, destination, port, localPort);
        return await StartProcessAndWaitAsync(start, localPort, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<OpenSshTunnel> StartProcessAndWaitAsync(ProcessStartInfo start, int localPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        var limits = new ExtendedLimits();
        limits.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, including app crash/exit.
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>())) {
            int error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error);
        }
        Process process = null;
        OpenSshTunnel tunnel = null;
        try {
            process = Process.Start(start) ?? throw new InvalidOperationException("Impossible de démarrer le tunnel SSH.");
            if (!AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
            tunnel = new OpenSshTunnel(process, job, localPort);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < timeout) {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) throw new InvalidOperationException("Le tunnel SSH s’est arrêté. Vérifiez le bastion, la clé et l’authentification dans la console Windows.");
                using var probe = new TcpClient(AddressFamily.InterNetwork);
                try {
                    await probe.ConnectAsync(IPAddress.Loopback, localPort, cancellationToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                    if (process.HasExited) throw new InvalidOperationException("Le tunnel SSH s’est arrêté pendant son ouverture.");
                    return tunnel;
                }
                catch (SocketException) { }
                catch (TimeoutException) { }
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("Le tunnel SSH n’est pas prêt après le délai d’authentification.");
        }
        catch {
            if (tunnel != null) tunnel.Dispose();
            else {
                try { if (process != null && !process.HasExited) process.Kill(entireProcessTree: true); }
                finally { process?.Dispose(); job.Dispose(); }
            }
            throw;
        }
    }

    internal static async Task<string> ResolveSshHostnameAsync(ConnectionInfo info, CancellationToken cancellationToken)
    {
        var start = WindowsSecurityPolicy.BuildOpenSshStartInfo(info.Hostname, info.Username, info.Port, info.SSHOptions);
        start.Arguments = "-G " + start.Arguments;
        start.CreateNoWindow = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Impossible de lire le profil SSH.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string config = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Le profil SSH de destination est invalide.");
            foreach (string line in config.Split('\n'))
                if (line.StartsWith("hostname ", StringComparison.Ordinal) && WindowsSecurityPolicy.IsSafeSshToken(line.Substring(9).Trim())) return line.Substring(9).Trim();
            throw new InvalidOperationException("Le profil SSH ne fournit pas de nom de serveur valide.");
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    internal static SafeFileHandle ProtectProcessLifetime(Process process)
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        try {
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000;
            if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()) || !AssignProcessToJobObject(job, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return job;
        } catch { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } finally { job.Dispose(); } throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { /* Closing the job below is the kernel-enforced fallback. */ }
        finally { _job.Dispose(); _process.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits limits, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
