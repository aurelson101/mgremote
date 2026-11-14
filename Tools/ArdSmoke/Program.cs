using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using VncSharpCore;

internal static class Program
{
    [STAThread] private static void Main(string[] args)
    {
        Test(true, true); Test(false, true); Test(true, false);
        Directory.CreateDirectory(args[0]);
        File.WriteAllText(Path.Combine(args[0], "apple-ard-verification.json"), "{\"Result\":\"PASS\",\"NativeType30Handshake\":true,\"MacAccountCredentials\":true,\"WrongPasswordRejected\":true,\"DowngradeRejected\":true,\"FramebufferInitialized\":true,\"RealMacTested\":false}");
        Console.WriteLine("PASS: simulated Apple RFB 3.889/type-30 account authentication, wrong password, downgrade rejection and framebuffer initialization; no real Mac qualification.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(bool correctPassword, bool offersArd)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() => {
            using var socket = listener.AcceptTcpClient(); using var stream = socket.GetStream(); stream.ReadTimeout = stream.WriteTimeout = 10000;
            void Send(byte[] bytes) => stream.Write(bytes);
            byte[] Read(int count) { var bytes = new byte[count]; stream.ReadExactly(bytes); return bytes; }
            Send(Encoding.ASCII.GetBytes("RFB 003.889\n"));
            Check(Encoding.ASCII.GetString(Read(12)) == "RFB 003.008\n", "Apple greeting was not negotiated as RFB 3.8.");
            Send(new byte[] { 1, offersArd ? (byte)30 : (byte)2 });
            if (!offersArd) return;
            Check(Read(1)[0] == 30, "Client did not select Apple ARD.");
            byte[] modulus = Org.BouncyCastle.Crypto.Agreement.DHStandardGroups.rfc2409_1024.P.ToByteArrayUnsigned();
            var p = new BigInteger(modulus, true, true); var secret = new BigInteger(RandomNumberGenerator.GetBytes(32), true, true) + 2;
            byte[] Fixed(BigInteger value) { var encoded = value.ToByteArray(true, true); var output = new byte[modulus.Length]; encoded.CopyTo(output, output.Length - encoded.Length); return output; }
            Send(new byte[] { 0, 2, 0, 128 }); Send(modulus); Send(Fixed(BigInteger.ModPow(2, secret, p)));
            byte[] encrypted = Read(128), clientPublic = Read(128);
            byte[] shared = Fixed(BigInteger.ModPow(new BigInteger(clientPublic, true, true), secret, p));
            using var aes = Aes.Create(); aes.Key = MD5.HashData(shared);
            byte[] credentials = aes.DecryptEcb(encrypted, PaddingMode.None);
            string Credential(int offset) { int end = Array.IndexOf(credentials, (byte)0, offset, 64); Check(end >= 0, "Credentials not null terminated."); return Encoding.UTF8.GetString(credentials, offset, end - offset); }
            bool accepted = Credential(0) == "mac-user" && Credential(64) == "Mac password é";
            CryptographicOperations.ZeroMemory(shared); CryptographicOperations.ZeroMemory(credentials);
            Check(accepted == correctPassword, "The simulated Mac decoded unexpected account credentials.");
            Send(new byte[] { 0, 0, 0, accepted ? (byte)0 : (byte)1 });
            if (!accepted) { Send(new byte[4]); return; }
            Check(Read(1)[0] == 1, "Missing shared desktop initialization.");
            Send(new byte[] { 0, 1, 0, 1, 32, 24, 0, 1, 0, 255, 0, 255, 0, 255, 16, 8, 0, 0, 0, 0 });
            byte[] name = Encoding.UTF8.GetBytes("Simulated MacBook"); Send(new byte[] { 0, 0, 0, (byte)name.Length }); Send(name);
            Read(44); // Five encodings and the client's pixel-format request.
        });
        var client = new VncClient { AppleUsername = "mac-user" };
        try {
            bool pending;
            try { pending = client.Connect("127.0.0.1", 0, port, false); }
            catch (VncProtocolException) when (!offersArd) { server.Wait(TimeSpan.FromSeconds(15)); return; }
            Check(offersArd && pending, "Apple mode permitted a security downgrade.");
            bool authenticated = client.Authenticate(correctPassword ? "Mac password é" : "wrong-password");
            Check(authenticated == correctPassword, "Wrong Apple authentication result.");
            if (authenticated) { client.Initialize(32, 24); Check(client.Framebuffer.Width == 1 && client.Framebuffer.Height == 1 && client.Framebuffer.DesktopName == "Simulated MacBook", "Mac framebuffer was not initialized."); }
            Check(server.Wait(TimeSpan.FromSeconds(15)), "Simulated Mac server timed out.");
        } finally { try { client.Disconnect(); } catch (Exception) { } }
    }
}
