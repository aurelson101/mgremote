// MGRemote. GPL-2.0; see ../COPYING.txt.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MGRemote.Networking;

public sealed record LanAdapter(string Name, string Address, int Prefix, string Mac)
{
    public override string ToString() => Name + " — " + Address + "/" + Prefix;
}
public sealed record LanDevice(string Address, string Mac, string Hostname, string OperatingSystem, string Evidence);

public static class LanDiscovery
{
    public static LanAdapter[] Adapters() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && Private(a.Address))
            .Select(a => new LanAdapter(n.Name, a.Address.ToString(), a.PrefixLength, n.GetPhysicalAddress().ToString())))
        .ToArray();

    public static bool Private(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254);
    }
    // A user-selected private interface, never more than its containing /24.
    public static IPAddress[] Scope(LanAdapter adapter)
    {
        if (!IPAddress.TryParse(adapter.Address, out var own) || !Private(own) || adapter.Prefix is < 0 or > 32) throw new ArgumentException("Choisissez une interface IPv4 privée valide.");
        byte[] bytes = own.GetAddressBytes(); uint number = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        int prefix = Math.Max(24, adapter.Prefix); uint mask = uint.MaxValue << (32 - prefix); uint first = number & mask;
        uint actualMask = adapter.Prefix == 0 ? 0 : uint.MaxValue << (32 - adapter.Prefix);
        uint network = number & actualMask, broadcast = network | ~actualMask;
        int count = 1 << (32 - prefix); var result = new List<IPAddress>();
        for (int i = 0; i < count; i++) {
            uint n = first + (uint)i;
            if (adapter.Prefix <= 30 && (n == network || n == broadcast)) continue;
            result.Add(new IPAddress(new[] { (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n }));
        }
        return result.ToArray();
    }

    public static async Task<LanDevice[]> DiscoverAsync(LanAdapter adapter, bool probe, IProgress<string> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scope = Scope(adapter); var allowed = scope.Select(x => x.ToString()).ToHashSet(StringComparer.Ordinal);
        var ttl = new ConcurrentDictionary<string, int>();
        if (probe) {
            progress?.Report("Recherche locale — " + scope.Length + " adresses maximum…");
            await Parallel.ForEachAsync(scope, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = token }, async (ip, ct) => {
                using var ping = new Ping();
                try {
                    var reply = await ping.SendPingAsync(ip, TimeSpan.FromMilliseconds(500), Array.Empty<byte>(), new PingOptions(64, true), ct).ConfigureAwait(false);
                    if (reply.Status == IPStatus.Success) ttl[ip.ToString()] = reply.Options?.Ttl ?? 0;
                } catch (PingException) { } catch (SocketException) { }
            }).ConfigureAwait(false);
        }
        var neighbors = await Neighbors(adapter, token).ConfigureAwait(false);
        var addresses = neighbors.Keys.Concat(ttl.Keys).Append(adapter.Address).Where(allowed.Contains).Distinct().ToArray();
        var devices = new ConcurrentBag<LanDevice>();
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (ip, ct) => {
            string hostname = "";
            try { hostname = (await Dns.GetHostEntryAsync(ip, AddressFamily.InterNetwork, ct).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false)).HostName; }
            catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException) { }
            bool local = ip == adapter.Address; int hop = ttl.GetValueOrDefault(ip);
            string os = local ? RuntimeInformation.OSDescription : Guess(hostname, hop);
            devices.Add(new LanDevice(ip, local ? FormatMac(adapter.Mac) : neighbors.GetValueOrDefault(ip, ""), hostname, os,
                local ? "Poste local (confirmé)" : hop > 0 ? "ICMP ; TTL " + hop + " ; OS indicatif" : "Voisin ARP ; OS indicatif"));
            progress?.Report("Résolution des noms — " + devices.Count + "/" + addresses.Length);
        }).ConfigureAwait(false);
        return devices.OrderBy(d => IPAddress.Parse(d.Address).GetAddressBytes(), Comparer<byte[]>.Create((a,b) => { for (int i=0;i<4;i++) {int n=a[i].CompareTo(b[i]);if(n!=0)return n;}return 0; })).ToArray();
    }
    public static string Guess(string hostname, int ttl)
    {
        string name = hostname.ToLowerInvariant();
        if (new[] { "macbook", "imac", "mac-mini", "macmini", "mac-studio" }.Any(name.Contains)) return "macOS probable (nom)";
        if (new[] { "ubuntu", "debian", "fedora", "linux", "raspberry" }.Any(name.Contains)) return "Linux probable (nom)";
        if (name.StartsWith("desktop-") || name.StartsWith("win-") || name.StartsWith("windows")) return "Windows probable (nom)";
        if (ttl is > 64 and <= 128) return "Windows possible (TTL)";
        if (ttl is > 0 and <= 64) return "Linux/macOS/autre (TTL)";
        return "Indéterminé";
    }
    private static string FormatMac(string value) => value.Length == 12 ? string.Join(":", Enumerable.Range(0,6).Select(i => value.Substring(i*2,2))).ToUpperInvariant() : value.ToUpperInvariant();
    private static async Task<Dictionary<string,string>> Neighbors(LanAdapter adapter, CancellationToken token)
    {
        string executable = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "arp.exe") : File.Exists("/usr/sbin/ip") ? "/usr/sbin/ip" : "/usr/bin/ip";
        var result = new Dictionary<string,string>();
        if (!File.Exists(executable)) return result;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in OperatingSystem.IsWindows() ? new[] { "-a", "-N", adapter.Address } : new[] { "-4", "neigh", "show", "dev", adapter.Name }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Impossible de lire les voisins réseau.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); string text = await output.ConfigureAwait(false); await error.ConfigureAwait(false);
            foreach (string line in text.Split('\n')) {
                var ip = Regex.Match(line, @"\b(?:\d{1,3}\.){3}\d{1,3}\b"); var mac = Regex.Match(line, @"\b(?:[0-9a-fA-F]{2}[:-]){5}[0-9a-fA-F]{2}\b");
                if (ip.Success && mac.Success && IPAddress.TryParse(ip.Value, out _) && mac.Value is not "00-00-00-00-00-00" and not "00:00:00:00:00:00" and not "ff-ff-ff-ff-ff-ff" and not "ff:ff:ff:ff:ff:ff") result[ip.Value] = mac.Value.Replace('-', ':').ToUpperInvariant();
            }
            return result;
        } finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
