using System;
using System.IO;
using System.Threading;
using YayoPont.Setup;
using Xunit;

namespace YayoPont.Tests
{
    public sealed class UpdaterTests : IDisposable
    {
        private readonly string folder = Path.Combine(Path.GetTempPath(), "yayo-maj-" + Guid.NewGuid().ToString("N"));
        private static readonly string BuiltExe = typeof(Api).Assembly.Location; // YayoPont.exe 1.1.0

        public void Dispose()
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        private static string Release(string tag, string asset = "YayoPont.exe") =>
            "{\"tag_name\":\"" + tag + "\",\"assets\":[{\"name\":\"autre.zip\",\"browser_download_url\":\"https://github.com/x/autre.zip\"},"
            + "{\"name\":\"" + asset + "\",\"browser_download_url\":\"https://github.com/Sharkooss/yayoextractor/releases/download/" + tag + "/" + asset + "\"}]}";

        private Updater Make(string current, string json, bool busy = false, string disabled = null)
        {
            var updater = new Updater(current, () => busy, folder, disabled)
            {
                FetchText = url => json,
                Download = (url, path) => File.Copy(BuiltExe, path, true),
                LaunchDelay = TimeSpan.Zero,
            };
            return updater;
        }

        [Fact]
        public void Lit_la_release_du_pont()
        {
            Assert.True(Updater.TryParseRelease(Release("pont-v1.2.3"), out var version, out var url));
            Assert.Equal(new Version(1, 2, 3), version);
            Assert.EndsWith("/YayoPont.exe", url);
            Assert.False(Updater.TryParseRelease(Release("v9.0.0"), out _, out _));
            Assert.False(Updater.TryParseRelease(Release("pont-v1.2.3", "Autre.exe"), out _, out _));
            Assert.False(Updater.TryParseRelease("pas du json", out _, out _));
            Assert.False(Updater.TryParseRelease(Release("pont-v1.2.3").Replace("https://github.com/Sharkooss", "https://evil.example"), out _, out _));
        }

        [Fact]
        public void Deja_a_jour()
        {
            Assert.Equal("uptodate", Make("1.1.0", Release("pont-v1.1.0")).Check()["state"]);
            Assert.Equal("uptodate", Make("1.2.0", Release("pont-v1.1.0")).Check()["state"]);
        }

        [Fact]
        public void Attend_la_fin_d_une_analyse_ou_d_une_copie()
        {
            var result = Make("1.0.1", Release("pont-v1.1.0"), busy: true).Check();
            Assert.Equal("busy", result["state"]);
            Assert.Equal("1.1.0", result["version"]);
            Assert.False(Directory.Exists(folder));
        }

        [Fact]
        public void Telecharge_verifie_et_lance_l_installation_silencieuse()
        {
            string launched = null;
            using (var done = new ManualResetEventSlim())
            {
                var updater = Make("1.0.1", Release("pont-v1.1.0"));
                updater.Launch = exe => { launched = exe; done.Set(); };
                var result = updater.Check();
                Assert.Equal("started", result["state"]);
                Assert.Equal("1.1.0", result["version"]);
                Assert.True(updater.Updating);
                Assert.True(done.Wait(5000));
            }
            Assert.Equal(Path.Combine(folder, "YayoPont.exe"), launched);
            Assert.False(File.Exists(launched + ".part"));
        }

        [Fact]
        public void Refuse_un_fichier_qui_n_est_pas_la_version_annoncee()
        {
            var updater = Make("1.0.1", Release("pont-v1.2.0"));
            updater.Launch = exe => throw new InvalidOperationException("ne doit pas être lancé");
            var result = updater.Check();
            Assert.Equal("error", result["state"]);
            Assert.Contains("version", (string)result["error"]);

            var tiny = Make("1.0.1", Release("pont-v1.1.0"));
            tiny.Download = (url, path) => File.WriteAllBytes(path, new byte[] { (byte)'M', (byte)'Z', 0 });
            Assert.Equal("error", tiny.Check()["state"]);
            Assert.False(tiny.Updating);
        }

        [Fact]
        public void Echec_reseau_sans_effet_et_simulation_refusee()
        {
            var offline = Make("1.0.1", null);
            offline.FetchText = url => throw new System.Net.WebException("hors ligne");
            Assert.Equal("error", offline.Check()["state"]);
            Assert.False(offline.Updating);

            var simulation = Make("1.0.1", Release("pont-v1.1.0"), disabled: "Pas de mise à jour en mode simulation.");
            var result = simulation.Check();
            Assert.Equal("error", result["state"]);
            Assert.Equal("Pas de mise à jour en mode simulation.", result["error"]);
        }
    }
}
