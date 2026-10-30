using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace MGRemote.Linux;

internal sealed class CredentialBroker : IDisposable
{
    private const string SocketVariable = "MGREMOTE_CREDENTIAL_SOCKET", TokenVariable = "MGREMOTE_CREDENTIAL_TOKEN";
    private readonly Socket _listener;
    private readonly CancellationTokenSource _lifetime;
    private readonly string _directory, _socketPath, _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly string _expectedPrompt, _protocol;
    private string _password;
    private int _disposed;
    internal Task Completion { get; }
    private sealed record Request(string Token, string Kind, string Prompt);

    internal CredentialBroker(Connection connection, string password)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Authentification intégrée : Linux requis.");
        if (password.Length > 4096 || password.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ArgumentException("Mot de passe incompatible avec l’authentification intégrée.");
        if (connection.Protocol is not ("SSH" or "RDP") || (connection.Protocol == "RDP" && (connection.User.Length == 0 || connection.GatewayHost.Length > 0)))
            throw new NotSupportedException("Pour RDP, renseignez l’utilisateur. Les passerelles utilisent encore l’authentification manuelle.");
        _lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _password = password; _protocol = connection.Protocol;
        _expectedPrompt = (connection.User.Length > 0 ? connection.User : Environment.UserName) + "@" + connection.Host + "'s password:";
        _directory = Path.Combine(Path.GetTempPath(), "mgremote-auth-" + Guid.NewGuid().ToString("N"));
        _socketPath = Path.Combine(_directory, "auth.sock");
        Directory.CreateDirectory(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try {
            _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _listener.Listen(4); Completion = Serve();
        } catch { _listener.Dispose(); File.Delete(_socketPath); Directory.Delete(_directory); _lifetime.Dispose(); throw; }
    }
    private async Task Serve()
    {
        try {
            for (int attempts = 0; attempts < 4; attempts++) {
                using var client = await _listener.AcceptAsync(_lifetime.Token);
                using var stream = new NetworkStream(client);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var request = await Read<Request>(stream, requestTimeout.Token);
                if (request == null) throw new JsonException();
                bool allowed = request.Token == _token && ((_protocol == "RDP" && request.Kind == "rdp") ||
                    (_protocol == "SSH" && request.Kind == "ssh" && request.Prompt.Trim() == _expectedPrompt));
                await Write(stream, allowed ? _password : null, requestTimeout.Token);
                if (allowed) break; // One password delivery per connection attempt.
            }
        } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or JsonException) { }
        finally { _password = ""; _listener.Dispose(); CleanupPaths(); }
    }
    internal void Configure(ProcessStartInfo start, string protocol)
    {
        if (protocol != _protocol) throw new ArgumentException("Protocole d’authentification incohérent.");
        start.Environment[SocketVariable] = _socketPath; start.Environment[TokenVariable] = _token;
        if (protocol == "SSH") {
            start.Environment["SSH_ASKPASS"] = Path.Combine(AppContext.BaseDirectory, "mgremote-askpass");
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            for (int i = 0; i < start.ArgumentList.Count; i++) if (start.ArgumentList[i] == "StrictHostKeyChecking=ask") start.ArgumentList[i] = "StrictHostKeyChecking=yes";
            int executable = start.ArgumentList.IndexOf("-e") + 1;
            if (executable < 1) throw new ArgumentException("Terminal SSH requis.");
            start.ArgumentList.Insert(executable + 1, "-o"); start.ArgumentList.Insert(executable + 2, "NumberOfPasswordPrompts=1");
            start.ArgumentList.Insert(executable + 3, "-o"); start.ArgumentList.Insert(executable + 4, "PreferredAuthentications=publickey,password");
        } else {
            int executable = start.ArgumentList.IndexOf("-e") + 1;
            if (executable < 1) throw new ArgumentException("Terminal RDP requis.");
            string client = start.ArgumentList[executable]; start.ArgumentList[executable] = Path.Combine(AppContext.BaseDirectory, "MGRemoteLinux");
            start.ArgumentList.Insert(executable + 1, "--credential-rdp"); start.ArgumentList.Insert(executable + 2, client);
        }
    }
    private static async Task Write<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 16384) throw new IOException("Message d’authentification trop volumineux.");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        try { await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task<T> Read<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size < 1 || size > 16384) throw new IOException("Message d’authentification invalide.");
        byte[] bytes = new byte[size];
        try { await stream.ReadExactlyAsync(bytes, token); return JsonSerializer.Deserialize<T>(bytes)!; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static async Task<string?> Receive(string kind, string prompt)
    {
        string path = Environment.GetEnvironmentVariable(SocketVariable) ?? "", token = Environment.GetEnvironmentVariable(TokenVariable) ?? "";
        if (path.Length == 0 || token.Length != 64 || prompt.Length > 4096) return null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token);
        using var stream = new NetworkStream(socket);
        await Write(stream, new Request(token, kind, prompt), deadline.Token);
        return await Read<string?>(stream, deadline.Token);
    }
    internal static async Task<int> RunHelper(string[] args)
    {
        try {
            bool rdp = args[0] == "--credential-rdp";
            string? password = await Receive(rdp ? "rdp" : "ssh", rdp ? "" : args.ElementAtOrDefault(1) ?? "");
            if (password == null) { Console.Error.WriteLine("Authentification intégrée refusée. Utilisez le mode manuel pour vérifier l’hôte, le bastion ou déverrouiller la clé."); return 1; }
            if (!rdp) { Console.WriteLine(password); return 0; }
            if (args.Length < 2) return 1;
            var start = new ProcessStartInfo(args[1]) { UseShellExecute = false, RedirectStandardInput = true };
            foreach (string arg in args.Skip(2)) start.ArgumentList.Add(arg);
            start.Environment.Remove(SocketVariable); start.Environment.Remove(TokenVariable);
            using var process = Process.Start(start) ?? throw new IOException();
            await process.StandardInput.WriteLineAsync(password); process.StandardInput.Close(); password = null;
            await process.WaitForExitAsync(); return process.ExitCode;
        } catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or JsonException or System.ComponentModel.Win32Exception) {
            Console.Error.WriteLine("Authentification intégrée indisponible. Reconnectez-vous en mode manuel."); return 1;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _password = ""; _lifetime.Cancel(); _listener.Dispose();
        CleanupPaths();
        _ = Completion.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
    }
    private void CleanupPaths()
    {
        try { File.Delete(_socketPath); Directory.Delete(_directory); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
