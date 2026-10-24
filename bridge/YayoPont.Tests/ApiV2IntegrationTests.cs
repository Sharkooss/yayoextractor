using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using YayoPont.Drives;
using YayoPont.Http;
using YayoPont.Phone;
using YayoPont.Setup;
using Xunit;

namespace YayoPont.Tests
{
    /// <summary>
    /// Essai de bout en bout de l'API v2 en simulation, sur un port libre : deux
    /// téléphones (dossiers), une clé (dossier), puis un dossier du PC.
    /// </summary>
    public sealed class ApiV2IntegrationTests : IDisposable
    {
        private const string Site = "http://localhost:8124";
        private readonly string work = Path.Combine(Path.GetTempPath(), "yayo-v2-" + Guid.NewGuid().ToString("N"));
        private readonly HttpServer server;
        private readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private readonly byte[] still = MediaFactory.JpegWithExif("2025:08:15 12:00:00", 43.2965, 5.3698);
        private readonly byte[] motion;
        private Api api;
        private string token;

        private sealed class FakePicker : IFolderPicker
        {
            public string Result;
            public int Calls;
            public string Pick() { Calls++; Thread.Sleep(200); return Result; }
        }

        private readonly FakePicker picker = new FakePicker();

        private string PhoneA => Path.Combine(work, "telephone-yayo");
        private string PhoneB => Path.Combine(work, "telephone-louis");
        private string Key => Path.Combine(work, "cle");

        public ApiV2IntegrationTests()
        {
            motion = MediaFactory.SamsungMotion(still, MediaFactory.Mp4(2, mdatBytes: 40000));
            Write(PhoneA, "DCIM/Camera/20250815_120000.jpg", motion);                              // photo animée
            Write(PhoneA, "DCIM/Camera/IMG_7.HEIC", MediaFactory.Heic("2025:08:16 09:00:00"));    // photo + clip
            Write(PhoneA, "DCIM/Camera/IMG_7.MOV", MediaFactory.Mp4(2));
            Write(PhoneA, "DCIM/Camera/VID_9.mp4", MediaFactory.Mp4(30, mdatBytes: 8000));
            Write(PhoneA, "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg", MediaFactory.Jpeg(40, 40));
            Write(PhoneA, "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/.nomedia", new byte[0]);
            Write(PhoneA, "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/IMG-2.jpg", MediaFactory.Jpeg(41, 40));
            Write(PhoneA, "WhatsApp/Media/WhatsApp Images/IMG-1.jpg", MediaFactory.Jpeg(40, 40)); // doublon (ancien dossier)
            Write(PhoneB, "DCIM/Camera/B.jpg", MediaFactory.Jpeg(50, 50));
            Directory.CreateDirectory(Key);

            server = new HttpServer(0, req => api.Handle(req));
            server.Start();
            api = new Api(new Security(server.Port), new FolderPhoneSource(new[] { PhoneA, PhoneB }), new FakeDriveProvider(Key), "1.1.0")
            {
                Picker = picker,
                Updater = new Updater("1.1.0", () => false, Path.Combine(work, "maj"), "Pas de mise à jour en mode simulation."),
            };
        }

        public void Dispose()
        {
            server.Dispose();
            http.Dispose();
            foreach (var file in Directory.GetFiles(work, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(work, true);
        }

        private static void Write(string root, string relative, byte[] data)
        {
            var path = Path.Combine(root, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        private Dictionary<string, object> Call(HttpMethod method, string path, string body = null, HttpStatusCode expected = HttpStatusCode.OK)
        {
            var req = new HttpRequestMessage(method, $"http://127.0.0.1:{server.Port}{path}");
            req.Headers.Add("Origin", Site);
            if (token != null) req.Headers.Add("X-Yayo-Token", token);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var res = http.SendAsync(req).Result;
            var text = res.Content.ReadAsStringAsync().Result;
            Assert.True(expected == res.StatusCode, $"{method} {path} → {(int)res.StatusCode} {text}");
            return (Dictionary<string, object>)Json.Parse(text);
        }

        private Dictionary<string, object> Poll(string path)
        {
            for (int i = 0; i < 200; i++)
            {
                var job = Call(HttpMethod.Get, path);
                if ((string)job["state"] != "running") return job;
                Thread.Sleep(50);
            }
            throw new TimeoutException(path);
        }

        private static List<Dictionary<string, object>> List(object value) => ((List<object>)value).Cast<Dictionary<string, object>>().ToList();

        private static string ToJson(object value) => Json.Serialize(value);

        [Fact]
        public void Parcours_v2_complet_en_simulation()
        {
            var status = Call(HttpMethod.Get, "/status");
            Assert.Equal(2d, status["api"]);
            Assert.Equal(false, status["updating"]);
            Assert.Equal(new[] { "phones", "folders", "layout", "deep", "repair", "update", "pick-folder" }, ((List<object>)status["features"]).Cast<string>().ToArray());
            token = (string)Call(HttpMethod.Post, "/session")["token"];

            // Deux téléphones, le premier choisi par défaut.
            var devices = Call(HttpMethod.Get, "/devices?all=1");
            var phones = List(devices["phones"]);
            Assert.Equal(2, phones.Count);
            Assert.Equal("test-1", ((Dictionary<string, object>)devices["phone"])["id"]);
            Assert.Equal("usb", List(devices["drives"]).Single()["kind"]);

            // Mode normal (Yayo) : clé, téléphone par défaut.
            var scanId = (string)Call(HttpMethod.Post, "/scan", "{\"driveId\":\"TEST\"}")["jobId"];
            var scan = Poll("/scan/" + scanId);
            Assert.Equal("done", scan["state"]);
            Assert.Equal(1d, scan["hidden"]);
            Assert.Equal(1d, scan["live"]);
            Assert.Equal(1d, scan["duplicates"]);
            var items = List(scan["items"]);
            Assert.Equal(7, items.Count);
            var normal = items.Where(i => !(bool)i["hidden"] && !(bool)i["live"] && i["duplicateOf"] == null).ToList();
            Assert.Equal(new[] { "20250815_120000.jpg", "IMG-1.jpg", "IMG_7.HEIC", "VID_9.mp4" }, normal.Select(i => (string)i["name"]).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            var ids = ToJson(normal.Select(i => i["id"]).ToList());

            var plan = Call(HttpMethod.Post, "/plan", "{\"driveId\":\"TEST\",\"ids\":" + ids + "}");
            Assert.Equal(4d, plan["toCopy"]);
            Assert.Empty(Directory.GetFileSystemEntries(Key));

            var transferId = (string)Call(HttpMethod.Post, "/transfer", "{\"driveId\":\"TEST\",\"ids\":" + ids + "}")["jobId"];
            var transfer = Poll("/transfer/" + transferId);
            Assert.Equal("done", transfer["state"]);
            Assert.Equal("copy", transfer["phase"]);
            Assert.Equal(4d, transfer["copied"]);
            Assert.Empty((List<object>)transfer["failed"]);
            Assert.Equal(0d, ((Dictionary<string, object>)transfer["repair"])["duplicates"]);
            var copied = List(transfer["copiedFiles"]);
            Assert.Contains(copied, c => (string)c["path"] == "Photos du téléphone/2025/08 - Août/20250815_120000.jpg");
            var photo = Path.Combine(Key, "Photos du téléphone", "2025", "08 - Août", "20250815_120000.jpg");
            Assert.Equal(still, File.ReadAllBytes(photo)); // la vidéo de la photo animée est retirée
            Assert.True(File.Exists(Path.Combine(Key, "Photos du téléphone", ".yayo-pont.tsv")));
            Assert.Empty(Directory.GetFiles(Key, "IMG_7.MOV", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(Key, "IMG-2.jpg", SearchOption.AllDirectories));

            // Deuxième analyse : tout ce qui est visible est déjà sur la clé.
            var rescan = Poll("/scan/" + (string)Call(HttpMethod.Post, "/scan", "{\"driveId\":\"TEST\"}")["jobId"]);
            Assert.All(List(rescan["items"]).Where(i => normal.Any(n => (string)n["id"] == (string)i["id"])), i => Assert.Equal(true, i["onDrive"]));

            // Mode avancé : deuxième téléphone, analyse approfondie, dossier du PC choisi au sélecteur.
            var pc = Path.Combine(work, "Sauvegarde PC");
            Directory.CreateDirectory(pc);
            picker.Result = pc;
            var picked = Call(HttpMethod.Post, "/pick-folder");
            Assert.Equal(pc, picked["path"]);
            picker.Result = null;
            Assert.Equal(true, Call(HttpMethod.Post, "/pick-folder")["cancelled"]);

            var folderJson = ToJson(pc);
            var deepId = (string)Call(HttpMethod.Post, "/scan", "{\"folder\":" + folderJson + ",\"phoneId\":\"test-2\",\"options\":{\"deep\":true,\"rootName\":\"Téléphone de Louis\"}}")["jobId"];
            var deep = Poll("/scan/" + deepId);
            Assert.Equal("done", deep["state"]);
            Assert.Equal(1d, deep["detailsTotal"]);
            Assert.Equal("test-2", ((Dictionary<string, object>)Call(HttpMethod.Get, "/devices")["phone"])["id"]);
            var b = List(deep["items"]).Single();
            Assert.Equal("B.jpg", b["name"]);

            var options = "{\"rootName\":\"Téléphone de Louis\",\"layout\":\"{lieu}/{annee}\",\"fileName\":\"{date} {nom}\",\"places\":{\"DCIM/Camera/B.jpg\":{\"lieu\":\"Marseille\"}},\"verify\":true,\"conflict\":\"rename\"}";
            var body = "{\"folder\":" + folderJson + ",\"ids\":[\"DCIM/Camera/B.jpg\"],\"options\":" + options + "}";
            var plan2 = Call(HttpMethod.Post, "/plan", body);
            var folder = List(plan2["folders"]).Single();
            Assert.StartsWith("Téléphone de Louis/Marseille/", (string)folder["path"]);
            var t2 = Poll("/transfer/" + (string)Call(HttpMethod.Post, "/transfer", body)["jobId"]);
            Assert.Equal("done", t2["state"]);
            var written = List(t2["copiedFiles"]).Single();
            Assert.Equal(folder["path"] + "/" + Path.GetFileName((string)written["path"]), written["path"]);
            Assert.True(File.Exists(Path.Combine(pc, ((string)written["path"]).Replace('/', '\\'))));

            // Copie vers une autre destination que celle analysée : refusée.
            Call(HttpMethod.Post, "/transfer", "{\"driveId\":\"TEST\",\"ids\":[\"DCIM/Camera/B.jpg\"]}", expected: HttpStatusCode.Conflict);
            // Dossier interdit, chemin réseau.
            Call(HttpMethod.Post, "/scan", "{\"folder\":" + ToJson(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + "}", expected: HttpStatusCode.BadRequest);
            Call(HttpMethod.Post, "/scan", "{\"folder\":\"\\\\\\\\serveur\\\\partage\"}", expected: HttpStatusCode.BadRequest);

            // Pas de mise à jour en simulation.
            var update = Call(HttpMethod.Post, "/update");
            Assert.Equal("error", update["state"]);
            Assert.Equal(2, picker.Calls);
        }

        [Fact]
        public void Un_seul_selecteur_de_dossier_a_la_fois()
        {
            token = (string)Call(HttpMethod.Post, "/session")["token"];
            picker.Result = work;
            var first = new Thread(() => Call(HttpMethod.Post, "/pick-folder"));
            first.Start();
            Thread.Sleep(50);
            Call(HttpMethod.Post, "/pick-folder", expected: HttpStatusCode.Conflict);
            Call(HttpMethod.Get, "/status"); // le serveur répond pendant que le sélecteur est ouvert
            first.Join();
        }
    }
}
