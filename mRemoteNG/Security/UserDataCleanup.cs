// MGRemote modification, 2026-10-29. GPL-2.0; see COPYING.txt.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace mRemoteNG.Security
{
    public static class UserDataCleanup
    {
        // The executable supplies its current Windows profile. No CLI path is accepted.
        public static void RemoveProfileData(string profileDirectory)
        {
            string profile = Path.GetFullPath(profileDirectory).TrimEnd(Path.DirectorySeparatorChar);
            if (profile.Length == 0 || string.Equals(profile, Path.GetPathRoot(profile), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing an invalid Windows profile directory.");
            CheckPath(profile, profile);
            var directories = new List<string>
            {
                Path.Combine(profile, "AppData", "Roaming", "MGRemote"),
                Path.Combine(profile, "AppData", "Local", "MGRemote"),
                Path.Combine(profile, ".ssh", "mgremote")
            };
            string[] companyDirectories = {
                Path.Combine(profile, "AppData", "Local", "MGRemote contributors"),
                Path.Combine(profile, "AppData", "Roaming", "MGRemote contributors")
            };
            foreach (string company in companyDirectories)
            {
                CheckPath(company, profile);
                if (Directory.Exists(company))
                    foreach (string child in Directory.GetDirectories(company, "MGRemote*", SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileName(child);
                        if (name.StartsWith("MGRemote.exe_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("MGRemote_", StringComparison.OrdinalIgnoreCase)) directories.Add(child);
                    }
            }
            string sshDirectory = Path.Combine(profile, ".ssh");
            string sshConfig = Path.Combine(sshDirectory, "config");
            CheckPath(sshDirectory, profile);
            CheckPath(sshConfig, profile);
            var backups = new List<string>();
            if (Directory.Exists(sshDirectory))
                foreach (string file in Directory.GetFiles(sshDirectory, "config.mgremote-*.bak", SearchOption.TopDirectoryOnly))
                    if (Regex.IsMatch(Path.GetFileName(file), "\\Aconfig\\.mgremote-[a-fA-F0-9]{32}\\.bak\\z")) backups.Add(file);
            // Preflight every known tree before changing anything. Never follow junctions/symlinks.
            foreach (string directory in directories) ValidateTree(directory, profile);
            foreach (string file in backups) CheckPath(file, profile);
            if (File.Exists(sshConfig)) RemoveOwnedSshIncludes(sshConfig, Path.Combine(sshDirectory, "mgremote"), profile);
            foreach (string directory in directories) DeleteTree(directory, profile);
            foreach (string file in backups) { CheckPath(file, profile); File.Delete(file); }
            foreach (string company in companyDirectories)
                if (Directory.Exists(company) && Directory.GetFileSystemEntries(company).Length == 0) Directory.Delete(company, false);
        }

        private static void RemoveOwnedSshIncludes(string configPath, string ownedDirectory, string profile)
        {
            CheckPath(configPath, profile);
            using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                byte[] original;
                using (var snapshot = new MemoryStream()) { stream.CopyTo(snapshot); original = snapshot.ToArray(); }
                var encoding = new UTF8Encoding(false, true);
                int offset = original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf ? 3 : 0;
                string text = encoding.GetString(original, offset, original.Length - offset);
                var kept = new StringBuilder();
                foreach (string line in Regex.Split(text, "(?<=\\n)"))
                {
                    var include = Regex.Match(line.TrimEnd('\r', '\n'), "\\A\\s*Include\\s+\"([^\"]+)\"\\s*\\z", RegexOptions.IgnoreCase);
                    bool owned = false;
                    if (include.Success && Path.IsPathRooted(include.Groups[1].Value))
                    {
                        string path = Path.GetFullPath(include.Groups[1].Value);
                        owned = string.Equals(Path.GetDirectoryName(path), ownedDirectory, StringComparison.OrdinalIgnoreCase) &&
                            Regex.IsMatch(Path.GetFileName(path), "\\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\\.conf\\z");
                    }
                    if (!owned) kept.Append(line);
                }
                if (kept.ToString() == text) return;
                byte[] updated = encoding.GetBytes(kept.ToString());
                try
                {
                    stream.Position = offset;
                    stream.Write(updated, 0, updated.Length);
                    stream.SetLength(stream.Position);
                    stream.Flush(true);
                }
                catch
                {
                    stream.Position = 0;
                    stream.Write(original, 0, original.Length);
                    stream.SetLength(original.Length);
                    stream.Flush(true);
                    throw;
                }
            }
        }

        private static void CheckPath(string path, string profile)
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(full, profile, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Refusing cleanup outside the current Windows profile.");
            for (string current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Close MGRemote and remove directory redirection before requesting user-data cleanup.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (string.Equals(current, profile, StringComparison.OrdinalIgnoreCase)) break;
            }
        }

        private static void ValidateTree(string path, string profile)
        {
            CheckPath(path, profile);
            if (!Directory.Exists(path)) return;
            foreach (string child in Directory.GetDirectories(path)) ValidateTree(child, profile);
            foreach (string file in Directory.GetFiles(path)) CheckPath(file, profile);
        }

        private static void DeleteTree(string path, string profile)
        {
            CheckPath(path, profile);
            if (!Directory.Exists(path)) return;
            foreach (string child in Directory.GetDirectories(path)) DeleteTree(child, profile);
            foreach (string file in Directory.GetFiles(path)) { CheckPath(file, profile); File.Delete(file); }
            CheckPath(path, profile);
            Directory.Delete(path, false);
        }
    }
}
