// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace mRemoteNG.Security;

public static class AnyDeskLauncher
{
    public static bool IsValidTarget(string target) => target != null && Regex.IsMatch(target,
        "\\A(?:[0-9]{6,12}|[A-Za-z0-9][A-Za-z0-9._-]{0,63}@[A-Za-z0-9][A-Za-z0-9._-]{0,63})\\z");

    public static string FindInstalledClient()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string directory = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(directory)) continue;
            string path = Path.Combine(directory, "AnyDesk", "AnyDesk.exe");
            if (File.Exists(path)) return path;
        }
        return string.Empty; // Never search the working directory or an untrusted PATH.
    }

    public static ProcessStartInfo BuildStartInfo(string executable, string target)
    {
        target = (target ?? string.Empty).Trim();
        if (!IsValidTarget(target)) throw new ArgumentException("Indiquez un ID AnyDesk ou un alias (exemple : poste@ad), sans options supplémentaires.");
        if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathFullyQualified(executable) || !File.Exists(executable) ||
            !Path.GetFileName(executable).StartsWith("AnyDesk", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Sélectionnez le client AnyDesk.exe installé ou un client personnalisé AnyDesk-*.exe approuvé.");
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)) };
        start.ArgumentList.Add(target);
        return start;
    }
}
