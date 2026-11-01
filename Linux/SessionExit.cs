using System.Diagnostics;
using System.Globalization;

namespace MGRemote.Linux;

// xterm -hold retains the journal after its client exits. Track that client separately.
internal sealed class SessionExit : IDisposable
{
    private readonly string _directory;
    internal SessionExit()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux requis.");
        _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mgremote-session-" + Guid.NewGuid().ToString("N")), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute).FullName;
    }
    private string Report => Path.Combine(_directory, "exit-code");
    internal void Wrap(ProcessStartInfo start, int command)
    {
        var args = start.ArgumentList.Skip(command).ToArray();
        while (start.ArgumentList.Count > command) start.ArgumentList.RemoveAt(command);
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "MGRemoteLinux"));
        start.ArgumentList.Add("--session-runner"); start.ArgumentList.Add(Report);
        foreach (string arg in args) start.ArgumentList.Add(arg);
    }
    internal int? Read()
    {
        if (!File.Exists(Report)) return null;
        try {
            using var file = new FileStream(Report, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (file.Length is < 1 or > 16) return null;
            using var reader = new StreamReader(file);
            return int.TryParse(reader.ReadToEnd(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) ? code : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsLinux() || args.Length < 3 || !Path.IsPathFullyQualified(args[1])) return 125;
        var parent = new DirectoryInfo(Path.GetDirectoryName(args[1])!);
        if (!parent.Exists || parent.LinkTarget != null || !parent.Name.StartsWith("mgremote-session-", StringComparison.Ordinal) ||
            File.GetUnixFileMode(parent.FullName) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) return 125;
        int code;
        try {
            var start = new ProcessStartInfo(args[2]) { UseShellExecute = false };
            foreach (string arg in args.Skip(3)) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Client indisponible.");
            process.WaitForExit(); code = process.ExitCode;
        } catch (Exception ex) { Console.Error.WriteLine("Démarrage impossible : " + ex.Message); code = 127; }
        string pending = args[1] + ".pending";
        using (var file = new FileStream(pending, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
        using (var writer = new StreamWriter(file)) writer.Write(code.ToString(CultureInfo.InvariantCulture));
        File.Move(pending, args[1]);
        return code;
    }
    public void Dispose()
    {
        try { File.Delete(Report); File.Delete(Report + ".pending"); Directory.Delete(_directory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
