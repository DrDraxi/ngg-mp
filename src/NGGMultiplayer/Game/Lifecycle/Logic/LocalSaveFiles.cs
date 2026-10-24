using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NGGMultiplayer.Core.Save;

namespace NGGMultiplayer.Game.Lifecycle
{
    /// <summary>One protected save file as it was when the backup was taken.</summary>
    internal sealed class SaveFileEntry
    {
        public SaveFileEntry(string name, bool existed, long length, uint crc)
        {
            Name = name;
            Existed = existed;
            Length = length;
            Crc = crc;
        }

        public string Name { get; }

        public bool Existed { get; }

        public long Length { get; }

        public uint Crc { get; }
    }

    /// <summary>A backup of the player's own save files (folder plus manifest).</summary>
    internal sealed class SaveBackup
    {
        public SaveBackup(string folder, IReadOnlyList<SaveFileEntry> files)
        {
            Folder = folder;
            Files = files;
        }

        public string Folder { get; }

        public IReadOnlyList<SaveFileEntry> Files { get; }
    }

    /// <summary>What <see cref="LocalSaveFiles.Restore"/> found and fixed.</summary>
    internal sealed class RestoreReport
    {
        public List<string> Changed { get; } = new List<string>();

        public List<string> Appeared { get; } = new List<string>();

        public List<string> Vanished { get; } = new List<string>();

        /// <summary>True when every file was exactly as backed up.</summary>
        public bool Clean => Changed.Count == 0 && Appeared.Count == 0 && Vanished.Count == 0;
    }

    /// <summary>
    /// The player's own save files on disk (docs/GAME_INTEGRATION.md 5.5, 5.6): backs them up with a CRC manifest
    /// before a session, verifies and repairs them afterwards, and manages the session sandbox the game's save paths
    /// are redirected to. The backup root and the sandbox must lie outside the save folder (Steam Cloud syncs it).
    /// Pure System.IO (unit tested); main thread only.
    /// </summary>
    internal sealed class LocalSaveFiles
    {
        public const string ManifestName = "manifest.txt";
        public const string ChangedSuffix = ".changed-during-session";
        public const int DefaultKeepBackups = 5;

        /// <summary>The save files a session may never change (config.txt stays the player's own and writable).</summary>
        public static readonly string[] ProtectedNames =
        {
            "run2.txt", "run2_temp.txt", "actuallyNormal.txt", "scoreCard.txt", "unlocks.txt"
        };

        private const string ManifestHeader = "NGGMultiplayer local save backup v1";
        private const string RestoreLogName = "restore-log.txt";
        private const string TempSuffix = ".nggmp-tmp";
        private const string FolderFormat = "yyyyMMdd-HHmmss";
        private static readonly Regex BackupFolderPattern = new Regex(@"^\d{8}-\d{6}(-\d+)?$", RegexOptions.CultureInvariant);

        private readonly int _keepBackups;

        public LocalSaveFiles(string saveDirectory, string backupRoot, string sandboxDirectory, int keepBackups = DefaultKeepBackups)
        {
            if (string.IsNullOrEmpty(saveDirectory))
                throw new ArgumentException("The save directory is required.", nameof(saveDirectory));
            if (string.IsNullOrEmpty(backupRoot))
                throw new ArgumentException("The backup root is required.", nameof(backupRoot));
            if (string.IsNullOrEmpty(sandboxDirectory))
                throw new ArgumentException("The sandbox directory is required.", nameof(sandboxDirectory));
            SaveDirectory = Path.GetFullPath(saveDirectory);
            BackupRoot = Path.GetFullPath(backupRoot);
            SandboxDirectory = Path.GetFullPath(sandboxDirectory);
            if (IsSameOrInside(BackupRoot, SaveDirectory) || IsSameOrInside(SaveDirectory, BackupRoot))
                throw new ArgumentException("The backup root must not overlap the save directory.", nameof(backupRoot));
            if (IsSameOrInside(SandboxDirectory, SaveDirectory) || IsSameOrInside(SaveDirectory, SandboxDirectory))
                throw new ArgumentException("The sandbox must not overlap the save directory.", nameof(sandboxDirectory));
            _keepBackups = Math.Max(1, keepBackups);
        }

        public string SaveDirectory { get; }

        public string BackupRoot { get; }

        public string SandboxDirectory { get; }

        public static bool IsProtectedName(string fileName)
        {
            foreach (var name in ProtectedNames)
            {
                if (string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>The sandbox path of <paramref name="fileName"/> (its file name part only).</summary>
        public string SandboxFile(string fileName)
        {
            string name = Path.GetFileName(fileName ?? string.Empty);
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("A file name is required.", nameof(fileName));
            return Path.Combine(SandboxDirectory, name);
        }

        /// <summary>
        /// Copies every protected file that exists into a new folder under <see cref="BackupRoot"/>, writes the
        /// manifest and keeps the newest backups. Any IO error throws (and removes the partial folder).
        /// </summary>
        public SaveBackup Backup(DateTime now)
        {
            Directory.CreateDirectory(BackupRoot);
            string folder = UniqueFolder(now.ToString(FolderFormat, CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            var entries = new List<SaveFileEntry>(ProtectedNames.Length);
            try
            {
                foreach (var name in ProtectedNames)
                {
                    string source = Path.Combine(SaveDirectory, name);
                    if (!File.Exists(source))
                    {
                        entries.Add(new SaveFileEntry(name, false, 0, 0));
                        continue;
                    }
                    byte[] bytes = File.ReadAllBytes(source);
                    uint crc = Crc32.Compute(bytes);
                    string target = Path.Combine(folder, name);
                    File.WriteAllBytes(target, bytes);
                    byte[] copy = File.ReadAllBytes(target);
                    if (copy.Length != bytes.Length || Crc32.Compute(copy) != crc)
                        throw new IOException($"The backup copy of {name} does not match the original.");
                    entries.Add(new SaveFileEntry(name, true, bytes.Length, crc));
                }
                File.WriteAllText(Path.Combine(folder, ManifestName), FormatManifest(entries, now), Encoding.UTF8);
            }
            catch
            {
                TryDeleteFolder(folder);
                throw;
            }
            Prune(folder);
            return new SaveBackup(folder, entries);
        }

        /// <summary>
        /// Brings the save folder back to the backup: a changed or new file is moved into the backup folder as
        /// "&lt;name&gt;.changed-during-session" and the backed-up copy put back; a vanished file is restored.
        /// Throws <see cref="IOException"/> when a file could not be put right.
        /// </summary>
        public RestoreReport Restore(SaveBackup backup)
        {
            if (backup == null)
                throw new ArgumentNullException(nameof(backup));
            var report = new RestoreReport();
            var problems = new List<string>();
            foreach (var entry in backup.Files)
            {
                string real = Path.Combine(SaveDirectory, entry.Name);
                try
                {
                    bool exists = File.Exists(real);
                    if (entry.Existed && exists)
                    {
                        byte[] bytes = File.ReadAllBytes(real);
                        if (bytes.Length == entry.Length && Crc32.Compute(bytes) == entry.Crc)
                            continue;
                        report.Changed.Add(entry.Name);
                        MoveAside(real, backup.Folder, entry.Name);
                        CopyBack(backup, entry, real);
                    }
                    else if (entry.Existed)
                    {
                        report.Vanished.Add(entry.Name);
                        CopyBack(backup, entry, real);
                    }
                    else if (exists)
                    {
                        report.Appeared.Add(entry.Name);
                        MoveAside(real, backup.Folder, entry.Name);
                    }
                }
                catch (Exception e) when (IsIoFailure(e))
                {
                    problems.Add($"{entry.Name}: {e.Message}");
                }
            }
            if (!report.Clean)
                TryWriteRestoreLog(backup.Folder, report, problems);
            if (problems.Count > 0)
                throw new IOException("Could not restore the save files: " + string.Join("; ", problems));
            return report;
        }

        /// <summary>Deletes everything in the sandbox (it is recreated on the next write).</summary>
        public void ClearSandbox()
        {
            if (!Directory.Exists(SandboxDirectory))
                return;
            foreach (var file in Directory.GetFiles(SandboxDirectory))
                File.Delete(file);
            foreach (var directory in Directory.GetDirectories(SandboxDirectory))
                Directory.Delete(directory, true);
        }

        public void WriteSandbox(string fileName, byte[] contents)
        {
            Directory.CreateDirectory(SandboxDirectory);
            File.WriteAllBytes(SandboxFile(fileName), contents ?? Array.Empty<byte>());
        }

        /// <summary>Reads a manifest written by <see cref="Backup"/> (for a backup folder from an earlier run).</summary>
        public static SaveBackup ReadManifest(string folder)
        {
            var lines = File.ReadAllLines(Path.Combine(folder, ManifestName), Encoding.UTF8);
            if (lines.Length == 0 || lines[0] != ManifestHeader)
                throw new IOException("Not a save backup manifest.");
            var entries = new List<SaveFileEntry>();
            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length == 2 && parts[1] == "missing" && IsProtectedName(parts[0]))
                {
                    entries.Add(new SaveFileEntry(parts[0], false, 0, 0));
                }
                else if (parts.Length == 3 && IsProtectedName(parts[0])
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long length)
                    && uint.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint crc))
                {
                    entries.Add(new SaveFileEntry(parts[0], true, length, crc));
                }
            }
            return new SaveBackup(folder, entries);
        }

        private static string FormatManifest(List<SaveFileEntry> entries, DateTime now)
        {
            var builder = new StringBuilder();
            builder.Append(ManifestHeader).Append('\n');
            builder.Append("# taken ").Append(now.ToString("u", CultureInfo.InvariantCulture)).Append('\n');
            foreach (var entry in entries)
            {
                builder.Append(entry.Name).Append('\t');
                if (entry.Existed)
                    builder.Append(entry.Length.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(entry.Crc.ToString("X8", CultureInfo.InvariantCulture));
                else
                    builder.Append("missing");
                builder.Append('\n');
            }
            return builder.ToString();
        }

        private static void MoveAside(string real, string folder, string name)
        {
            string target = Path.Combine(folder, name + ChangedSuffix);
            for (int i = 1; File.Exists(target); i++)
                target = Path.Combine(folder, name + ChangedSuffix + "." + i.ToString(CultureInfo.InvariantCulture));
            File.Move(real, target);
        }

        private static void CopyBack(SaveBackup backup, SaveFileEntry entry, string real)
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(backup.Folder, entry.Name));
            if (bytes.Length != entry.Length || Crc32.Compute(bytes) != entry.Crc)
                throw new IOException($"the backup copy of {entry.Name} in {backup.Folder} is damaged");
            // Written beside the target and moved into place, so a failed write never leaves a truncated save.
            string temp = real + TempSuffix;
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, real);
        }

        private string UniqueFolder(string baseName)
        {
            string folder = Path.Combine(BackupRoot, baseName);
            for (int i = 2; Directory.Exists(folder); i++)
                folder = Path.Combine(BackupRoot, baseName + "-" + i.ToString(CultureInfo.InvariantCulture));
            return folder;
        }

        private void Prune(string keep)
        {
            try
            {
                var folders = new List<string>();
                foreach (var directory in Directory.GetDirectories(BackupRoot))
                {
                    if (BackupFolderPattern.IsMatch(Path.GetFileName(directory)))
                        folders.Add(directory);
                }
                // Names sort by time; a "-n" suffix (same second) sorts after its base, which is also its order.
                folders.Sort((a, b) => CompareBackupNames(Path.GetFileName(b), Path.GetFileName(a)));
                for (int i = _keepBackups; i < folders.Count; i++)
                {
                    if (!string.Equals(folders[i], keep, StringComparison.Ordinal))
                        TryDeleteFolder(folders[i]);
                }
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                // Old backups piling up is no reason to refuse a session.
                Core.Diagnostics.NetLog.Warn($"[Lifecycle] Pruning old save backups failed: {e.Message}");
            }
        }

        private static int CompareBackupNames(string a, string b)
        {
            int c = string.CompareOrdinal(a.Substring(0, 15), b.Substring(0, 15));
            if (c != 0)
                return c;
            return SuffixOf(a).CompareTo(SuffixOf(b));
        }

        private static int SuffixOf(string name) =>
            name.Length > 16 && int.TryParse(name.Substring(16), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 1;

        private static void TryWriteRestoreLog(string folder, RestoreReport report, List<string> problems)
        {
            try
            {
                var builder = new StringBuilder();
                builder.Append("Restore at ").Append(DateTime.Now.ToString("u", CultureInfo.InvariantCulture)).Append('\n');
                foreach (var name in report.Changed)
                    builder.Append("changed during the session (kept as ").Append(name).Append(ChangedSuffix).Append("), restored: ").Append(name).Append('\n');
                foreach (var name in report.Appeared)
                    builder.Append("appeared during the session (kept as ").Append(name).Append(ChangedSuffix).Append("): ").Append(name).Append('\n');
                foreach (var name in report.Vanished)
                    builder.Append("vanished during the session, restored: ").Append(name).Append('\n');
                foreach (var problem in problems)
                    builder.Append("FAILED: ").Append(problem).Append('\n');
                File.AppendAllText(Path.Combine(folder, RestoreLogName), builder.ToString(), Encoding.UTF8);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                Core.Diagnostics.NetLog.Warn($"[Lifecycle] Writing the restore log failed: {e.Message}");
            }
        }

        private static void TryDeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }
            catch (Exception e) when (IsIoFailure(e))
            {
                Core.Diagnostics.NetLog.Warn($"[Lifecycle] Deleting {folder} failed: {e.Message}");
            }
        }

        private static bool IsSameOrInside(string path, string root)
        {
            // Mono's profile lacks Path.TrimEndingDirectorySeparator.
            string a = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string b = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(b + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsIoFailure(Exception e) =>
            e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException
            || e is NotSupportedException || e is ArgumentException;
    }
}
