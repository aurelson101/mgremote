using System.Text.RegularExpressions;

namespace MGRemote.Linux;

public static class ConnectionIcons
{
    public static readonly string[] Choices = ["Auto", "Windows", "Linux", "Network", "Serial"];

    // Resolve from saved metadata only. Opening a profile never probes remote machines.
    public static string Resolve(Connection connection)
    {
        if (connection.Icon is "Windows" or "Linux" or "Network" or "Serial") return connection.Icon;
        if (connection.Protocol == "Serial") return "Serial";
        string label = connection.Name + " " + connection.Host + " " + connection.Description;
        bool windows = Has(label, "windows|win(?:10|11|2012|2016|2019|2022|2025)?|powershell");
        bool linux = Has(label, "linux|ubuntu|debian|fedora|centos|rhel|rocky|almalinux|alpine|opensuse|raspberrypi|wsl");
        if (windows != linux) return windows ? "Windows" : "Linux";
        if (!windows && Has(label, "router|switch|firewall|pfsense|opnsense|mikrotik|fortigate|esxi")) return "Network";
        return connection.Protocol == "RDP" ? "Windows" : connection.Protocol == "SSH" ? "Linux" : "Network";
    }

    private static bool Has(string value, string alternatives) => Regex.IsMatch(value,
        @"(?:^|[^a-z0-9])(?:" + alternatives + @")(?=$|[^a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string FromImportedIcon(string? icon)
    {
        string value = (icon ?? "").Replace('\\', '/').Split('/')[^1];
        if (value.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        return value.Trim().ToLowerInvariant() switch {
            "windows" or "remote desktop" or "terminal server" or "domain controller" or "powershell" or "sharepoint" => "Windows",
            "linux" or "ssh" or "putty" or "wsl" or "raspberrypi" => "Linux",
            "router" or "switch" or "firewall" or "wifi" or "esx" or "infrastructure" => "Network",
            "console" or "tel" => "Serial",
            _ => "Auto"
        };
    }
}

internal static class ConnectionIconTests
{
    internal static void Run()
    {
        void Check(bool condition) { if (!condition) throw new Exception("Connection icon detection failed."); }
        Check(ConnectionIcons.Resolve(new Connection { Protocol = "RDP" }) == "Windows");
        Check(ConnectionIcons.Resolve(new Connection { Protocol = "SSH" }) == "Linux");
        Check(ConnectionIcons.Resolve(new Connection { Protocol = "Serial" }) == "Serial");
        Check(ConnectionIcons.Resolve(new Connection { Protocol = "SSH", Name = "Windows-2025" }) == "Windows");
        Check(ConnectionIcons.Resolve(new Connection { Protocol = "RDP", Host = "ubuntu.example" }) == "Linux");
        Check(ConnectionIcons.Resolve(new Connection { Name = "Firewall PROD" }) == "Network");
        Check(ConnectionIcons.Resolve(new Connection { Protocol = "RDP", Description = "Windows / Linux" }) == "Windows");
        Check(ConnectionIcons.Resolve(new Connection { Protocol = "RDP", Icon = "Linux" }) == "Linux");
        Check(ConnectionIcons.Resolve(new Connection { Name = "Darwin" }) == "Linux");
        Check(ConnectionIcons.FromImportedIcon(@"C:\Icons\Windows.ico") == "Windows");
        Check(ConnectionIcons.FromImportedIcon("Linux") == "Linux");
        Check(ConnectionIcons.FromImportedIcon("mRemoteNG") == "Auto");
        Check(ConnectionIcons.FromImportedIcon("custom-icon") == "Auto");
        string fixture = Path.Combine(Path.GetTempPath(), "mgremote-icons-" + Guid.NewGuid().ToString("N") + ".xml");
        try {
            File.WriteAllText(fixture, "<Connections><Node Type='Container' Name='Servers' Icon='Linux'><Node Type='Connection' Name='Generic' Protocol='RDP' Hostname='server.example' InheritIcon='true'/></Node></Connections>");
            var imported = Connections.Import(fixture).Single();
            Check(imported.Icon == "Linux" && ConnectionIcons.Resolve(imported) == "Linux");
            var restored = System.Text.Json.JsonSerializer.Deserialize<Connection>(System.Text.Json.JsonSerializer.Serialize(imported))!;
            Check(restored.Icon == "Linux");
        } finally { File.Delete(fixture); }
        Console.WriteLine("PASS: automatic connection icons, explicit overrides and imported icon mapping; no remote probes.");
    }
}
