// MGRemote modification, GPL-2.0-only. See ../COPYING.txt.
// Wire format follows LibVNCClient HandleARDAuth (RFB security type 30).
using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace VncSharpCore;

internal static class AppleArdAuthentication
{
    internal static void Authenticate(BinaryReader reader, Action<byte[]> send, string username, string password)
    {
        byte[] user = Encoding.UTF8.GetBytes(username ?? ""), pass = Encoding.UTF8.GetBytes(password ?? "");
        byte[] secret = null, aesKey = null, credentials = null, exponent = null;
        try {
            if (user.Length == 0 || user.Length > 63 || pass.Length > 63 || username.Contains('\0') || password.Contains('\0'))
                throw new ArgumentException("Apple ARD requires a username and credentials of at most 63 UTF-8 bytes each.");
            int generator = (reader.ReadByte() << 8) | reader.ReadByte();
            int length = (reader.ReadByte() << 8) | reader.ReadByte();
            if (length < 128 || length > 512) throw new InvalidDataException("Unsupported Apple ARD DH parameter size.");
            byte[] modulus = Read(reader, length), peerBytes = Read(reader, length);
            var p = new BigInteger(modulus, true, true); var peer = new BigInteger(peerBytes, true, true);
            if (modulus[0] < 128 || generator < 2 || generator >= p - 1 || peer < 2 || peer >= p - 1 ||
                !new Org.BouncyCastle.Math.BigInteger(1, modulus).IsProbablePrime(64))
                throw new InvalidDataException("Invalid Apple ARD DH parameters.");
            exponent = RandomNumberGenerator.GetBytes(32);
            var privateKey = new BigInteger(exponent, true, true) + 2;
            byte[] publicKey = Fixed(BigInteger.ModPow(generator, privateKey, p), length);
            secret = Fixed(BigInteger.ModPow(peer, privateKey, p), length);
            if (new BigInteger(secret, true, true) <= 1) throw new InvalidDataException("Invalid Apple ARD shared secret.");
            // MD5/AES-ECB are dictated by the legacy Apple type-30 wire protocol.
            // They do not authenticate the server or encrypt the desktop stream.
            aesKey = MD5.HashData(secret);
            credentials = RandomNumberGenerator.GetBytes(128);
            user.CopyTo(credentials, 0); credentials[user.Length] = 0;
            pass.CopyTo(credentials, 64); credentials[64 + pass.Length] = 0;
            using var aes = Aes.Create(); aes.Key = aesKey;
            byte[] encrypted = aes.EncryptEcb(credentials, PaddingMode.None);
            byte[] response = new byte[128 + length];
            encrypted.CopyTo(response, 0); publicKey.CopyTo(response, 128); send(response);
        } finally {
            foreach (byte[] bytes in new[] { user, pass, secret, aesKey, credentials, exponent })
                if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
        }
    }
    private static byte[] Read(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count) throw new EndOfStreamException("Truncated Apple ARD authentication.");
        return bytes;
    }
    private static byte[] Fixed(BigInteger value, int length)
    {
        byte[] encoded = value.ToByteArray(true, true), output = new byte[length];
        if (encoded.Length > length) throw new InvalidDataException("Invalid Apple ARD integer size.");
        encoded.CopyTo(output, length - encoded.Length); return output;
    }
}
