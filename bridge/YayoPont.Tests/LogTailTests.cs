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
            // Le chemin du journal est global : d'autres tests, en parallèle, peuvent y écrire aussi.
            // On vérifie donc l'ordre et le plafond, pas l'exclusivité.
            for (int i = 1; i <= 5; i++) Log.Info("tail-test ligne " + i);
            var tail = Log.Tail(200);
            var mine = tail.Where(l => l.Contains("tail-test ligne")).ToList();
            Assert.Equal(5, mine.Count);
            Assert.EndsWith("ligne 1", mine[0]);
            Assert.EndsWith("ligne 5", mine[4]);
            Assert.True(Log.Tail(2).Length <= 2);
        }
    }
}
