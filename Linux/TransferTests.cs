namespace MGRemote.Linux;

internal static class TransferTests
{
    internal static void Run()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new Exception("Transfer check failed: " + checks); checks++; }
        void Reject(Action action) { try { action(); } catch (Exception ex) when (ex is IOException or ArgumentException) { checks++; return; } throw new Exception("Unsafe transfer accepted."); }
        string root = Path.Combine(Path.GetTempPath(), "mgremote-multi-transfer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            string file = Path.Combine(root, "file with spaces.txt"), folder = Path.Combine(root, "folder");
            File.WriteAllText(file, "synthetic"); Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "child.txt"), "synthetic child");
            string[] paths = FileTransfer.Sources(file + "\n" + folder + "\n" + file);
            Check(paths.Length == 2 && paths[0] == file);
            var connection = new Connection { Host = "2001:db8::1", User = "fixture" };
            var args = FileTransfer.UploadArguments(connection, paths, "/remote path/", true);
            Check(args.Contains("-r") && args.Contains("--") && args[^3] == file && args[^2] == folder && args[^1] == "[2001:db8::1]:/remote path/");
            Check(!args.Contains("-O") && !args.Contains("sh") && args.Contains("ClearAllForwardings=yes"));
            Reject(() => FileTransfer.UploadArguments(connection, paths, "/remote/file", true));
            Reject(() => FileTransfer.UploadArguments(connection, [], "/remote/", false));
            Reject(() => FileTransfer.UploadArguments(connection, Enumerable.Repeat(file, 101).ToArray(), "/remote/", false));
            Reject(() => FileTransfer.UploadArguments(connection, [file, Path.Combine(root, "other", Path.GetFileName(file))], "/remote/", false));
            Reject(() => FileTransfer.Sources("")); Reject(() => FileTransfer.Sources("relative-file"));
            Reject(() => FileTransfer.Sources(string.Join('\n', Enumerable.Range(0, 101).Select(i => Path.Combine(root, i.ToString())))));
            var plan = FileTransfer.PrepareUpload(connection, paths, "/remote/", true, CancellationToken.None).GetAwaiter().GetResult();
            Check(plan.Executable.EndsWith("/scp", StringComparison.Ordinal) && plan.Arguments.Contains(file) && plan.Arguments.Contains(folder));
            Reject(() => FileTransfer.PrepareUpload(connection, [folder], "/remote/", false, CancellationToken.None).GetAwaiter().GetResult());
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { FileTransfer.PrepareUpload(connection, paths, "/remote/", true, canceled.Token).GetAwaiter().GetResult(); throw new Exception("Canceled upload prepared a client."); } catch (OperationCanceledException) { checks++; }
            if (OperatingSystem.IsLinux()) {
                string link = Path.Combine(root, "link"); File.CreateSymbolicLink(link, file);
                Reject(() => FileTransfer.PrepareUpload(connection, [file, link], "/remote/", false, CancellationToken.None).GetAwaiter().GetResult());
            }
            Console.WriteLine($"PASS: {checks} multi-source SFTP checks: argument boundaries, recursive folders, collision/size/path guards, cancellation and safe local preparation; no remote transfer.");
        } finally { Directory.Delete(root, true); }
    }
}
