using System;
using System.IO;
using System.Linq;
using Xunit;

namespace YayoPont.Tests
{
    public sealed class LogTailTests : IDisposable
    {
        private readonly string original = Log.FilePath;
        private readonly string dir = Path.Combine(Path.GetTempPath(), "yayo-log-" + Guid.NewGuid().ToString("N"));

        public LogTailTests() => Log.FilePath = Path.Combine(dir, "pont.log");

        public void Dispose()
        {
            Log.FilePath = original;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        [Fact]
        public void Rend_les_dernieres_lignes_du_journal()
        {
            for (int i = 1; i <= 5; i++) Log.Info("ligne " + i);
            var tail = Log.Tail(2);
            Assert.Equal(2, tail.Length);
            Assert.EndsWith("ligne 4", tail[0]);
            Assert.EndsWith("ligne 5", tail[1]);
        }

        [Fact]
        public void Sans_journal_rend_une_liste_vide()
        {
            Assert.Empty(Log.Tail(10));
        }
    }
}
