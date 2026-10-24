using System;
using System.IO;
using System.Linq;
using System.Text;
using NGGMultiplayer.Game.Lifecycle;
using Xunit;

namespace NGGMultiplayer.Tests.GameLifecycle
{
    public sealed class LocalSaveFilesTests : IDisposable
    {
        private readonly string _root;
        private readonly string _saves;
        private readonly string _backups;
        private readonly string _sandbox;
        private readonly LocalSaveFiles _files;

        public LocalSaveFilesTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "nggmp-tests-" + Guid.NewGuid().ToString("N"));
            _saves = Path.Combine(_root, "LocalLow", "Normal Golf Game");
            _backups = Path.Combine(_root, "BepInEx", "cache", "NGGMultiplayer", "LocalBackup");
            _sandbox = Path.Combine(_root, "BepInEx", "cache", "NGGMultiplayer", "SessionSave");
            Directory.CreateDirectory(_saves);
            _files = new LocalSaveFiles(_saves, _backups, _sandbox);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        private void WriteSave(string name, string text) => File.WriteAllText(Path.Combine(_saves, name), text, Encoding.UTF8);

        private string ReadSave(string name) => File.ReadAllText(Path.Combine(_saves, name), Encoding.UTF8);

        private static readonly DateTime Noon = new DateTime(2026, 10, 20, 12, 0, 0);

        [Fact]
        public void Backup_CopiesExistingFilesAndRecordsMissingOnes()
        {
            WriteSave("run2.txt", "{\"own\":1}");
            WriteSave("unlocks.txt", "{\"selectedBall\":3}");
            WriteSave("config.txt", "settings");

            var backup = _files.Backup(Noon);

            Assert.Equal(Path.Combine(_backups, "20261020-120000"), backup.Folder);
            Assert.Equal("{\"own\":1}", File.ReadAllText(Path.Combine(backup.Folder, "run2.txt")));
            Assert.False(File.Exists(Path.Combine(backup.Folder, "config.txt")));
            Assert.Equal(LocalSaveFiles.ProtectedNames.Length, backup.Files.Count);
            Assert.True(backup.Files.Single(f => f.Name == "run2.txt").Existed);
            Assert.False(backup.Files.Single(f => f.Name == "actuallyNormal.txt").Existed);

            var fromDisk = LocalSaveFiles.ReadManifest(backup.Folder);
            Assert.Equal(backup.Files.Count, fromDisk.Files.Count);
            for (int i = 0; i < backup.Files.Count; i++)
            {
                Assert.Equal(backup.Files[i].Name, fromDisk.Files[i].Name);
                Assert.Equal(backup.Files[i].Existed, fromDisk.Files[i].Existed);
                Assert.Equal(backup.Files[i].Length, fromDisk.Files[i].Length);
                Assert.Equal(backup.Files[i].Crc, fromDisk.Files[i].Crc);
            }
        }

        [Fact]
        public void Restore_UnchangedFiles_IsClean()
        {
            WriteSave("run2.txt", "own run");
            var backup = _files.Backup(Noon);
            var report = _files.Restore(backup);
            Assert.True(report.Clean);
            Assert.Equal("own run", ReadSave("run2.txt"));
            Assert.Empty(Directory.GetFiles(backup.Folder, "*" + LocalSaveFiles.ChangedSuffix + "*"));
        }

        [Fact]
        public void Restore_PutsBackAChangedFileAndKeepsTheChangedCopy()
        {
            WriteSave("run2.txt", "own run");
            WriteSave("config.txt", "old settings");
            var backup = _files.Backup(Noon);
            WriteSave("run2.txt", "HOST RUN");
            WriteSave("config.txt", "new settings");

            var report = _files.Restore(backup);

            Assert.Equal(new[] { "run2.txt" }, report.Changed);
            Assert.Equal("own run", ReadSave("run2.txt"));
            Assert.Equal("HOST RUN", File.ReadAllText(Path.Combine(backup.Folder, "run2.txt" + LocalSaveFiles.ChangedSuffix)));
            // config.txt is the player's own and stays writable during a session.
            Assert.Equal("new settings", ReadSave("config.txt"));
            Assert.False(File.Exists(Path.Combine(_saves, "run2.txt.nggmp-tmp")));
        }

        [Fact]
        public void Restore_MovesAnAppearedFileAsideAndRestoresAVanishedOne()
        {
            WriteSave("unlocks.txt", "own unlocks");
            var backup = _files.Backup(Noon);
            File.Delete(Path.Combine(_saves, "unlocks.txt"));
            WriteSave("actuallyNormal.txt", "host golf");

            var report = _files.Restore(backup);

            Assert.Equal(new[] { "unlocks.txt" }, report.Vanished);
            Assert.Equal(new[] { "actuallyNormal.txt" }, report.Appeared);
            Assert.Equal("own unlocks", ReadSave("unlocks.txt"));
            Assert.False(File.Exists(Path.Combine(_saves, "actuallyNormal.txt")));
            Assert.True(File.Exists(Path.Combine(backup.Folder, "actuallyNormal.txt" + LocalSaveFiles.ChangedSuffix)));
        }

        [Fact]
        public void Restore_TwiceChanged_KeepsBothCopies()
        {
            WriteSave("run2.txt", "own");
            var backup = _files.Backup(Noon);
            WriteSave("run2.txt", "first");
            _files.Restore(backup);
            WriteSave("run2.txt", "second");
            _files.Restore(backup);
            Assert.Equal("own", ReadSave("run2.txt"));
            Assert.True(File.Exists(Path.Combine(backup.Folder, "run2.txt" + LocalSaveFiles.ChangedSuffix)));
            Assert.True(File.Exists(Path.Combine(backup.Folder, "run2.txt" + LocalSaveFiles.ChangedSuffix + ".1")));
        }

        [Fact]
        public void Restore_ThrowsWhenTheBackupCopyIsDamaged()
        {
            WriteSave("run2.txt", "own run");
            var backup = _files.Backup(Noon);
            File.Delete(Path.Combine(_saves, "run2.txt"));
            File.WriteAllText(Path.Combine(backup.Folder, "run2.txt"), "garbage");

            Assert.Throws<IOException>(() => _files.Restore(backup));
            Assert.False(File.Exists(Path.Combine(_saves, "run2.txt")));
        }

        [Fact]
        public void Backup_KeepsTheFiveNewest()
        {
            WriteSave("run2.txt", "own");
            Directory.CreateDirectory(_backups);
            Directory.CreateDirectory(Path.Combine(_backups, "not-a-backup"));
            for (int i = 0; i < 8; i++)
                _files.Backup(Noon.AddMinutes(i));

            var names = Directory.GetDirectories(_backups).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "20261020-120300", "20261020-120400", "20261020-120500", "20261020-120600", "20261020-120700", "not-a-backup"
            }, names);
        }

        [Fact]
        public void Backup_InTheSameSecond_GetsItsOwnFolder()
        {
            WriteSave("run2.txt", "own");
            var first = _files.Backup(Noon);
            var second = _files.Backup(Noon);
            Assert.NotEqual(first.Folder, second.Folder);
            Assert.EndsWith("-2", second.Folder);
            // Pruning orders "-n" folders after their base second.
            for (int i = 0; i < 5; i++)
                _files.Backup(Noon.AddSeconds(-10 - i));
            Assert.True(Directory.Exists(second.Folder));
        }

        [Fact]
        public void Sandbox_WritesByFileNameOnlyAndEmpties()
        {
            _files.WriteSandbox("run2.txt", Encoding.UTF8.GetBytes("host"));
            _files.WriteSandbox("../../escape.txt", Encoding.UTF8.GetBytes("x"));
            Assert.Equal("host", File.ReadAllText(Path.Combine(_sandbox, "run2.txt")));
            Assert.True(File.Exists(Path.Combine(_sandbox, "escape.txt")));
            Assert.Equal(Path.Combine(_sandbox, "unlocks.txt"), _files.SandboxFile(Path.Combine(_saves, "unlocks.txt")));

            Directory.CreateDirectory(Path.Combine(_sandbox, "nested"));
            _files.ClearSandbox();
            Assert.Empty(Directory.GetFileSystemEntries(_sandbox));
            _files.ClearSandbox();
        }

        [Fact]
        public void Folders_MayNotOverlapTheSaveFolder()
        {
            Assert.Throws<ArgumentException>(() => new LocalSaveFiles(_saves, Path.Combine(_saves, "backup"), _sandbox));
            Assert.Throws<ArgumentException>(() => new LocalSaveFiles(_saves, _backups, Path.Combine(_saves, "sandbox")));
            Assert.Throws<ArgumentException>(() => new LocalSaveFiles(_saves, _backups, _saves));
            Assert.Throws<ArgumentException>(() => new LocalSaveFiles(_saves, _root, _sandbox));
        }

        [Fact]
        public void ProtectedNames_LeaveConfigLocal()
        {
            Assert.True(LocalSaveFiles.IsProtectedName("run2.txt"));
            Assert.True(LocalSaveFiles.IsProtectedName("RUN2_TEMP.TXT"));
            Assert.True(LocalSaveFiles.IsProtectedName("scoreCard.txt"));
            Assert.False(LocalSaveFiles.IsProtectedName("config.txt"));
        }
    }
}
