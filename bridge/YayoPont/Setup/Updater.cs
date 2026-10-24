using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using YayoPont.Http;

namespace YayoPont.Setup
{
    /// <summary>
    /// Mise à jour automatique : au démarrage puis toutes les 12 h (et sur POST /update),
    /// lit la dernière release GitHub « pont-vX.Y.Z » ; plus récente et pont au repos,
    /// télécharge YayoPont.exe dans %LOCALAPPDATA%\YayoPont\maj, le vérifie, puis lance
    /// « YayoPont.exe --maj » (installation habituelle, sans ouvrir le site).
    /// Téléchargé par le pont lui-même, le fichier n'a pas la marque « venant
    /// d'internet » : pas de SmartScreen. Tout échec est loggé et sans effet.
    /// </summary>
    internal sealed class Updater : IDisposable
    {
        public const string LatestRelease = "https://api.github.com/repos/Sharkooss/yayoextractor/releases/latest";
        private const string TagPrefix = "pont-v";
        private const string AssetName = "YayoPont.exe";
        private const long MinBytes = 50 * 1024;
        private const long MaxBytes = 50L * 1024 * 1024;
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Period = TimeSpan.FromHours(12);
        private static readonly TimeSpan RetryWhenBusy = TimeSpan.FromMinutes(15);

        private readonly Version current;
        private readonly Func<bool> busy;
        private readonly string folder;
        private readonly string disabledReason;
        private Timer timer;
        private int checking;
        private volatile bool launched;

        // Remplaçables par les tests.
        internal Func<string, string> FetchText;
        internal Action<string, string> Download;
        internal Action<string> Launch;
        internal TimeSpan LaunchDelay = TimeSpan.FromSeconds(1);

        /// <param name="disabledReason">null si la mise à jour est permise, sinon pourquoi elle ne l'est pas.</param>
        public Updater(string currentVersion, Func<bool> busy, string folder, string disabledReason)
        {
            current = Normalize(Version.Parse(currentVersion));
            this.busy = busy;
            this.folder = folder;
            this.disabledReason = disabledReason;
            FetchText = DefaultFetchText;
            Download = DefaultDownload;
            Launch = exe => Process.Start(new ProcessStartInfo(exe, "--maj") { UseShellExecute = false, WorkingDirectory = folder });
        }

        /// <summary>Une mise à jour est en cours (téléchargement, ou nouvelle version lancée).</summary>
        public bool Updating => launched || Volatile.Read(ref checking) == 1;

        public void Start()
        {
            if (disabledReason != null) return;
            timer = new Timer(_ => Tick(), null, StartupDelay, Timeout.InfiniteTimeSpan);
        }

        public void Dispose() => timer?.Dispose();

        private void Tick()
        {
            var result = Check();
            var next = (string)result["state"] == "busy" ? RetryWhenBusy : Period;
            try { timer?.Change(next, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>→ {state: "started"|"uptodate"|"busy"|"error", version?, error?}</summary>
        public Dictionary<string, object> Check()
        {
            if (disabledReason != null) return Result("error", null, disabledReason);
            if (launched || Interlocked.CompareExchange(ref checking, 1, 0) != 0) return Result("started", null, null);
            try
            {
                var json = FetchText(LatestRelease);
                if (!TryParseRelease(json, out var version, out var url)) return Fail("réponse de GitHub inattendue", null);
                if (version <= current) return Result("uptodate", version, null);
                if (busy()) return Result("busy", version, null);

                Directory.CreateDirectory(folder);
                var exe = Path.Combine(folder, AssetName);
                var part = exe + ".part";
                Download(url, part);
                var problem = Verify(part, version);
                if (problem != null)
                {
                    TryDelete(part);
                    return Fail(problem, version);
                }
                if (File.Exists(exe)) File.Delete(exe);
                File.Move(part, exe);
                launched = true;
                Log.Info($"mise à jour {current} → {version}");
                new Thread(() =>
                {
                    Thread.Sleep(LaunchDelay); // le temps de répondre au site
                    try
                    {
                        Launch(exe);
                        // Toujours là après 2 min : l'installation a échoué, l'ancienne version continue.
                        Thread.Sleep(TimeSpan.FromMinutes(2));
                    }
                    catch (Exception ex) { Log.Error("lancement de la mise à jour", ex); }
                    launched = false;
                })
                { IsBackground = true, Name = "maj" }.Start();
                return Result("started", version, null);
            }
            catch (Exception ex)
            {
                Log.Error("mise à jour", ex);
                return Result("error", null, "La mise à jour a échoué : " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref checking, 0);
            }
        }

        private static Dictionary<string, object> Fail(string problem, Version version)
        {
            Log.Info("mise à jour abandonnée : " + problem);
            return Result("error", version, "La mise à jour a échoué : " + problem + ".");
        }

        private static Dictionary<string, object> Result(string state, Version version, string error)
        {
            var result = new Dictionary<string, object> { ["state"] = state };
            if (version != null) result["version"] = version.ToString(3);
            if (error != null) result["error"] = error;
            return result;
        }

        /// <summary>Tag « pont-vX.Y.Z » et adresse de l'exe dans la réponse de l'API GitHub.</summary>
        internal static bool TryParseRelease(string json, out Version version, out string url)
        {
            version = null;
            url = null;
            Dictionary<string, object> release;
            try { release = Json.Parse(json) as Dictionary<string, object>; }
            catch (Exception ex) when (ex is FormatException || ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException) { return false; }
            if (release == null || !(release.TryGetValue("tag_name", out var tag) && tag is string name) || !name.StartsWith(TagPrefix)) return false;
            if (!Version.TryParse(name.Substring(TagPrefix.Length), out var parsed)) return false;
            if (!(release.TryGetValue("assets", out var raw) && raw is List<object> assets)) return false;
            var asset = assets.OfType<Dictionary<string, object>>()
                .FirstOrDefault(a => a.TryGetValue("name", out var n) && (n as string) == AssetName);
            if (asset == null || !(asset.TryGetValue("browser_download_url", out var link) && link is string href)) return false;
            if (!href.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) return false;
            version = Normalize(parsed);
            url = href;
            return true;
        }

        /// <summary>null si le fichier est bien l'exe annoncé, sinon le problème.</summary>
        internal static string Verify(string path, Version expected)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinBytes) return "fichier trop petit";
            using (var stream = File.OpenRead(path))
            {
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z') return "ce n'est pas un programme Windows";
            }
            var fileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (!Version.TryParse(fileVersion ?? "", out var actual) || Normalize(actual) != expected)
                return $"version du fichier ({fileVersion}) différente de la version annoncée ({expected.ToString(3)})";
            return null;
        }

        private static Version Normalize(Version v) => new Version(v.Major, v.Minor, Math.Max(0, v.Build));

        private string DefaultFetchText(string url)
        {
            var request = Request(url);
            request.Accept = "application/vnd.github+json";
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private void DefaultDownload(string url, string path)
        {
            var request = Request(url);
            request.Accept = "application/octet-stream";
            using (var response = request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > MaxBytes) throw new IOException("fichier trop gros");
                    output.Write(buffer, 0, read);
                }
            }
        }

        private HttpWebRequest Request(string url)
        {
            // .NET Framework 4.8 n'active pas toujours TLS 1.2 tout seul : GitHub l'exige.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "YayoPont/" + current.ToString(3) + " (+https://yayoextractor.louis-nectoux.fr)";
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.AllowAutoRedirect = true;
            return request;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
