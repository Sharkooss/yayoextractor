using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using YayoPont.Core;
using YayoPont.Drives;
using YayoPont.Http;
using YayoPont.Phone;
using Xunit;

namespace YayoPont.Tests
{
    /// <summary>
    /// Essai de bout en bout du pont 1.1.1 sur un port libre : une clé remplie par le
    /// pont 1.0.1 (photos animées complètes, clip iPhone dans le mois voisin, doublons
    /// dont une photo animée déjà raccourcie, copies WhatsApp « Sent », vidéo tronquée)
    /// est rangée par la seule analyse, sans copie.
    /// </summary>
    public sealed class ApiRepairIntegrationTests : IDisposable
    {
        private const string Site = "http://localhost:8124";
        private const string Sent = "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/";
        private readonly string work = Path.Combine(Path.GetTempPath(), "yayo-111-" + Guid.NewGuid().ToString("N"));
        private readonly HttpServer server;
        private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private readonly Api api;
        private string token;

        private string PhoneDir => Path.Combine(work, "telephone");
        private string KeyDir => Path.Combine(work, "cle");
        private string Managed => Path.Combine(KeyDir, "Photos du téléphone");

        // Contenus.
        private readonly byte[] still1 = MediaFactory.JpegWithExif("2025:08:15 12:00:00");
        private readonly byte[] motion1;
        private readonly byte[] still2 = MediaFactory.JpegWithExif("2025:09:01 09:00:00");
        private readonly byte[] motion2;
        private readonly byte[] heic = MediaFactory.HeicImage("2025:10:31 23:59:30");
        private readonly byte[] clip = MediaFactory.Movie(2.8, 1440, 1080, appleLive: true, audio: 2.8);
        private readonly byte[] original, sentCopy, orphanSent;
        private readonly byte[] video = MediaFactory.Movie(30, 1920, 1080, audio: 30, mdatBytes: 60000);
        private readonly byte[] plain = MediaFactory.JpegWithExif("2025:10:02 08:00:00", 45.76, 4.83);

        public ApiRepairIntegrationTests()
        {
            motion1 = MediaFactory.WithSef(still1, ("Image_UTC_Data", 0x0A01, Encoding.ASCII.GetBytes("1755252000000")),
                ("MotionPhoto_Data", 0x0A30, MediaFactory.Mp4(2.5, mdatBytes: 40000)), ("MotionPhoto_Version", 0x0A31, Encoding.ASCII.GetBytes("1.4")));
            motion2 = MediaFactory.SamsungMotion(still2, MediaFactory.Mp4(2, mdatBytes: 30000));
            (original, sentCopy) = RepairV2Tests.PhotoPair(40);
            (_, orphanSent) = RepairV2Tests.PhotoPair(41);

            // Le téléphone.
            Phone("DCIM/Camera/20250815_120000.jpg", motion1, new DateTime(2025, 8, 15, 12, 0, 0));
            Phone("DCIM/Camera/20250901_090000.jpg", motion2, new DateTime(2025, 9, 1, 9, 0, 0));
            Phone("DCIM/Camera/20250820_100000.jpg", original, new DateTime(2025, 8, 20, 10, 0, 0));
            Phone("DCIM/Camera/20251002_080000.jpg", plain, new DateTime(2025, 10, 2, 8, 0, 0));
            Phone("DCIM/Camera/VID_9.mp4", video, new DateTime(2025, 9, 3, 9, 0, 0));
            Phone("DCIM/100APPLE/IMG_7.HEIC", heic, new DateTime(2025, 10, 31, 23, 59, 30));
            Phone("DCIM/100APPLE/IMG_7.MOV", clip, new DateTime(2025, 11, 1, 0, 0, 20));
            Phone(Sent + ".nomedia", new byte[0], null);
            Phone(Sent + "IMG-20250821-WA0001.jpg", sentCopy, new DateTime(2025, 8, 21, 18, 0, 0));
            Phone(Sent + "IMG-20250822-WA0002.jpg", orphanSent, new DateTime(2025, 8, 22, 18, 0, 0));

            // La clé, telle que le pont 1.0.1 (puis 1.1.0) l'a laissée.
            Key("2025/08 - Août/20250815_120000.jpg", motion1, new DateTime(2025, 8, 15, 12, 0, 0));
            var stripped = Key("2025/09 - Septembre/20250901_090000.jpg", still2, new DateTime(2025, 9, 1, 9, 0, 0)); // raccourcie par le 1.1.0
            CopyIndex.Append(Managed, "20250901_090000.jpg", motion2.Length, stripped, still2.Length);
            Key("2025/09 - Septembre/20250901_090000 (2).jpg", motion2, new DateTime(2025, 9, 1, 9, 0, 0));     // copie complète du 1.0.1
            Key("2025/10 - Octobre/IMG_7.HEIC", heic, new DateTime(2025, 10, 31, 23, 59, 30));
            Key("2025/11 - Novembre/IMG_7.MOV", clip, new DateTime(2025, 11, 1, 0, 0, 20));                     // clip dans le mois voisin
            Key("2025/08 - Août/20250820_100000.jpg", original, new DateTime(2025, 8, 20, 10, 0, 0));
            Key("2025/08 - Août/IMG-20250821-WA0001.jpg", sentCopy, new DateTime(2025, 8, 21, 18, 0, 0));      // original sur la clé
            Key("2025/08 - Août/IMG-20250822-WA0002.jpg", orphanSent, new DateTime(2025, 8, 22, 18, 0, 0));    // original absent
            Key("2025/09 - Septembre/VID_9.mp4", video.Take(video.Length - 20000).ToArray(), new DateTime(2025, 9, 3, 9, 0, 0)); // tronquée
            Key("2025/10 - Octobre/20251002_080000.jpg", plain, new DateTime(2025, 10, 2, 8, 0, 0));

            server = new HttpServer(0, req => api.Handle(req));
            server.Start();
            api = new Api(new Security(server.Port), new FolderPhoneSource(PhoneDir), new FakeDriveProvider(KeyDir), "1.1.1");
        }

        public void Dispose()
        {
            server.Dispose();
            http.Dispose();
            foreach (var file in Directory.GetFiles(work, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(work, true);
        }

        private void Phone(string relative, byte[] data, DateTime? date) => Save(PhoneDir, relative, data, date);

        private string Key(string relative, byte[] data, DateTime date) => Save(Managed, relative, data, date);

        private static string Save(string root, string relative, byte[] data, DateTime? date)
        {
            var path = Path.Combine(root, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            if (date.HasValue) File.SetLastWriteTime(path, date.Value);
            return path;
        }

        private string K(string relative) => Path.Combine(Managed, relative.Replace('/', '\\'));

        private Dictionary<string, object> Call(HttpMethod method, string path, string body = null)
        {
            var req = new HttpRequestMessage(method, $"http://127.0.0.1:{server.Port}{path}");
            req.Headers.Add("Origin", Site);
            if (token != null) req.Headers.Add("X-Yayo-Token", token);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var res = http.SendAsync(req).GetAwaiter().GetResult();
            var text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{method} {path} → {(int)res.StatusCode} {text}");
            return (Dictionary<string, object>)Json.Parse(text);
        }

        private Dictionary<string, object> Poll(string path, List<string> phases = null)
        {
            for (int i = 0; i < 1200; i++)
            {
                var job = Call(HttpMethod.Get, path);
                if (phases != null && job.TryGetValue("phase", out var phase) && (phases.Count == 0 || phases.Last() != (string)phase)) phases.Add((string)phase);
                if ((string)job["state"] != "running") return job;
                Thread.Sleep(10);
            }
            throw new TimeoutException(path);
        }

        private static List<Dictionary<string, object>> List(object value) => ((List<object>)value).Cast<Dictionary<string, object>>().ToList();

        private static double N(Dictionary<string, object> o, string key) => (double)o[key];

        private Dictionary<string, object> Scan()
        {
            var scan = Poll("/scan/" + (string)Call(HttpMethod.Post, "/scan", "{\"driveId\":\"TEST\"}")["jobId"]);
            Assert.Equal("done", scan["state"]);
            return scan;
        }

        private static List<Dictionary<string, object>> Visible(Dictionary<string, object> scan) =>
            List(scan["items"]).Where(i => !(bool)i["hidden"] && !(bool)i["live"] && i["duplicateOf"] == null).ToList();

        [Fact]
        public void L_analyse_seule_range_une_cle_du_pont_1_0_1()
        {
            token = (string)Call(HttpMethod.Post, "/session")["token"];
            var bytesBefore = Directory.GetFiles(Managed, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith(".yayo-pont")).Sum(f => new FileInfo(f).Length);

            // 1. Analyse seule, sans copie.
            var scan = Scan();
            var repair = (Dictionary<string, object>)scan["repair"];
            Assert.Equal(2, N(repair, "motionStripped")); // la complète et le doublon complet
            Assert.Equal(1, N(repair, "duplicates"));
            Assert.Equal(1, N(repair, "liveRemoved"));
            Assert.Equal(1, N(repair, "brokenRemoved"));
            Assert.Equal(1, N(repair, "sentRemoved"));
            Assert.Equal(0, N(repair, "partsRemoved"));
            var bytesAfter = Directory.GetFiles(Managed, "*", SearchOption.AllDirectories).Where(f => !Path.GetFileName(f).StartsWith(".yayo-pont")).Sum(f => new FileInfo(f).Length);
            Assert.Equal(bytesBefore - bytesAfter, N(repair, "bytesFreed"), 0);

            // La clé est rangée.
            Assert.Equal(still1, File.ReadAllBytes(K("2025/08 - Août/20250815_120000.jpg")));
            Assert.Equal(still2, File.ReadAllBytes(K("2025/09 - Septembre/20250901_090000.jpg")));
            Assert.False(File.Exists(K("2025/09 - Septembre/20250901_090000 (2).jpg")));
            Assert.False(Directory.Exists(K("2025/11 - Novembre")));
            Assert.False(File.Exists(K("2025/08 - Août/IMG-20250821-WA0001.jpg")));
            Assert.False(File.Exists(K("2025/09 - Septembre/VID_9.mp4")));
            // Ce qui doit rester est intact, octet pour octet.
            Assert.Equal(heic, File.ReadAllBytes(K("2025/10 - Octobre/IMG_7.HEIC")));
            Assert.Equal(original, File.ReadAllBytes(K("2025/08 - Août/20250820_100000.jpg")));
            Assert.Equal(orphanSent, File.ReadAllBytes(K("2025/08 - Août/IMG-20250822-WA0002.jpg")));
            Assert.Equal(plain, File.ReadAllBytes(K("2025/10 - Octobre/20251002_080000.jpg")));
            Assert.Equal(new DateTime(2025, 8, 15, 12, 0, 0), File.GetLastWriteTime(K("2025/08 - Août/20250815_120000.jpg")));

            // La galerie : tout est « déjà sur la clé », sauf la vidéo tronquée.
            var visible = Visible(scan);
            Assert.Equal(new[] { "20250815_120000.jpg", "20250820_100000.jpg", "20250901_090000.jpg", "20251002_080000.jpg", "IMG_7.HEIC", "VID_9.mp4" },
                visible.Select(i => (string)i["name"]).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(new[] { "VID_9.mp4" }, visible.Where(i => !(bool)i["onDrive"]).Select(i => (string)i["name"]).ToArray());
            Assert.True((bool)List(scan["items"]).Single(i => (string)i["name"] == "IMG_7.MOV")["live"]);

            // 2. Copie de ce qui manque.
            var ids = Json.Serialize(visible.Where(i => !(bool)i["onDrive"]).Select(i => i["id"]).ToList());
            var phases = new List<string>();
            var transfer = Poll("/transfer/" + (string)Call(HttpMethod.Post, "/transfer", "{\"driveId\":\"TEST\",\"ids\":" + ids + "}")["jobId"], phases);
            Assert.Equal("done", transfer["state"]);
            Assert.Equal("copy", phases.Last());
            Assert.Equal(1d, transfer["copied"]);
            Assert.Empty((List<object>)transfer["failed"]);
            Assert.Equal(0d, N((Dictionary<string, object>)transfer["repair"], "sentRemoved"));
            Assert.Equal(video, File.ReadAllBytes(K("2025/09 - Septembre/VID_9.mp4")));

            // 3. Seconde analyse : rien à refaire.
            var again = Scan();
            var repair2 = (Dictionary<string, object>)again["repair"];
            foreach (var key in new[] { "duplicates", "motionStripped", "liveRemoved", "brokenRemoved", "partsRemoved", "sentRemoved", "bytesFreed" })
                Assert.Equal(0d, repair2[key]);
            Assert.All(Visible(again), i => Assert.True((bool)i["onDrive"], (string)i["name"]));
            Assert.Equal(orphanSent, File.ReadAllBytes(K("2025/08 - Août/IMG-20250822-WA0002.jpg")));
        }

        [Fact]
        public void Analyse_sans_rangement_ne_touche_a_rien()
        {
            token = (string)Call(HttpMethod.Post, "/session")["token"];
            var before = Directory.GetFiles(Managed, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
            var scan = Poll("/scan/" + (string)Call(HttpMethod.Post, "/scan", "{\"driveId\":\"TEST\",\"options\":{\"repair\":false}}")["jobId"]);
            Assert.Equal("done", scan["state"]);
            Assert.Equal(0d, N((Dictionary<string, object>)scan["repair"], "motionStripped"));
            foreach (var pair in before) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));

            // stripMotion faux : rangement sans raccourcir ni retirer les clips.
            var scan2 = Poll("/scan/" + (string)Call(HttpMethod.Post, "/scan", "{\"driveId\":\"TEST\",\"options\":{\"stripMotion\":false}}")["jobId"]);
            var repair = (Dictionary<string, object>)scan2["repair"];
            Assert.Equal(0d, N(repair, "motionStripped"));
            Assert.Equal(0d, N(repair, "liveRemoved"));
            Assert.Equal(1d, N(repair, "sentRemoved"));
            Assert.Equal(motion1, File.ReadAllBytes(K("2025/08 - Août/20250815_120000.jpg")));
            Assert.True(File.Exists(K("2025/11 - Novembre/IMG_7.MOV")));
        }
    }
}
