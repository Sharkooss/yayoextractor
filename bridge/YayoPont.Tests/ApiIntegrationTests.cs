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
using Xunit;

namespace YayoPont.Tests
{
    public sealed class ApiIntegrationTests : IDisposable
    {
        private const string Site = "http://localhost:8124";
        private readonly string phoneDir = Path.Combine(Path.GetTempPath(), "yayo-api-tel-" + Guid.NewGuid().ToString("N"));
        private readonly string driveDir = Path.Combine(Path.GetTempPath(), "yayo-api-cle-" + Guid.NewGuid().ToString("N"));
        private readonly HttpServer server;
        private readonly HttpClient http = new HttpClient();
        private Api api;

        public ApiIntegrationTests()
        {
            var photo = Path.Combine(phoneDir, "DCIM", "Camera", "IMG_1.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(photo));
            File.WriteAllBytes(photo, PhoneSourceTests.Jpeg(640, 480));
            File.WriteAllBytes(Path.Combine(phoneDir, "DCIM", "Camera", "VID_1.mp4"), new byte[4096]);
            server = new HttpServer(0, req => api.Handle(req));
            server.Start();
            api = new Api(new Security(server.Port), new FolderPhoneSource(phoneDir), new FakeDriveProvider(driveDir), "1.0.0");
        }

        public void Dispose()
        {
            server.Dispose();
            http.Dispose();
            Directory.Delete(phoneDir, true);
            if (Directory.Exists(driveDir)) Directory.Delete(driveDir, true);
        }

        private string Url(string path) => $"http://127.0.0.1:{server.Port}{path}";

        private HttpResponseMessage Send(HttpMethod method, string path, string token = null, string body = null, string origin = Site)
        {
            var req = new HttpRequestMessage(method, Url(path));
            if (origin != null) req.Headers.Add("Origin", origin);
            if (token != null) req.Headers.Add("X-Yayo-Token", token);
            if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return http.SendAsync(req).Result;
        }

        private Dictionary<string, object> JsonOf(HttpResponseMessage res) =>
            (Dictionary<string, object>)Json.Parse(res.Content.ReadAsStringAsync().Result);

        private Dictionary<string, object> Poll(string path, string token)
        {
            for (int i = 0; i < 100; i++)
            {
                var job = JsonOf(Send(HttpMethod.Get, path, token));
                if ((string)job["state"] != "running") return job;
                Thread.Sleep(50);
            }
            throw new TimeoutException(path);
        }

        [Fact]
        public void Parcours_complet_session_analyse_copie_vignette_ejection()
        {
            var status = Send(HttpMethod.Get, "/status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.Equal(Site, status.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Equal(2d, JsonOf(status)["api"]);

            Assert.Equal(HttpStatusCode.Unauthorized, Send(HttpMethod.Get, "/devices").StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, Send(HttpMethod.Post, "/session", origin: "https://evil.com").StatusCode);
            var token = (string)JsonOf(Send(HttpMethod.Post, "/session"))["token"];

            var devices = JsonOf(Send(HttpMethod.Get, "/devices", token));
            Assert.Equal("ready", ((Dictionary<string, object>)devices["phone"])["state"]);
            var drive = (Dictionary<string, object>)((List<object>)devices["drives"]).Single();
            Assert.Equal("TEST", drive["id"]);

            var scanId = (string)JsonOf(Send(HttpMethod.Post, "/scan", token, "{\"driveId\":\"TEST\"}"))["jobId"];
            var scan = Poll("/scan/" + scanId, token);
            Assert.Equal("done", scan["state"]);
            var items = ((List<object>)scan["items"]).Cast<Dictionary<string, object>>().ToList();
            Assert.Equal(2, items.Count);

            var photoId = (string)items.Single(i => (string)i["kind"] == "photo")["id"];
            var thumb = Send(HttpMethod.Get, "/thumb/" + Uri.EscapeDataString(photoId) + "?t=" + token, origin: null);
            Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
            Assert.Equal("image/jpeg", thumb.Content.Headers.ContentType.MediaType);

            var media = Send(HttpMethod.Get, "/media/" + Uri.EscapeDataString(photoId), token);
            Assert.Equal(new FileInfo(Path.Combine(phoneDir, "DCIM", "Camera", "IMG_1.jpg")).Length, media.Content.ReadAsByteArrayAsync().Result.Length);

            var ids = string.Join(",", items.Select(i => "\"" + i["id"] + "\""));
            var transferId = (string)JsonOf(Send(HttpMethod.Post, "/transfer", token, "{\"driveId\":\"TEST\",\"ids\":[" + ids + "]}"))["jobId"];
            var transfer = Poll("/transfer/" + transferId, token);
            Assert.Equal("done", transfer["state"]);
            Assert.Equal(2d, transfer["copied"]);
            var written = Directory.GetFiles(Path.Combine(driveDir, "Photos du téléphone"), "*", SearchOption.AllDirectories);
            Assert.Equal(2, written.Count(f => Core.MediaRules.Kind(f) != null));
            Assert.Contains(written, f => Path.GetFileName(f) == ".yayo-pont.tsv");
            Assert.Equal(2, ((List<object>)transfer["copiedFiles"]).Count);

            var rescan = Poll("/scan/" + (string)JsonOf(Send(HttpMethod.Post, "/scan", token, "{\"driveId\":\"TEST\"}"))["jobId"], token);
            Assert.All((List<object>)rescan["items"], i => Assert.Equal(true, ((Dictionary<string, object>)i)["onDrive"]));

            var eject = JsonOf(Send(HttpMethod.Post, "/eject", token, "{\"driveId\":\"TEST\"}"));
            Assert.Equal(true, eject["ok"]);
            Assert.Empty((List<object>)JsonOf(Send(HttpMethod.Get, "/devices", token))["drives"]);
        }

        [Fact]
        public void Preflight_et_hote_etranger()
        {
            var pre = Send(HttpMethod.Options, "/scan");
            Assert.Equal(HttpStatusCode.NoContent, pre.StatusCode);
            Assert.Equal("true", pre.Headers.GetValues("Access-Control-Allow-Private-Network").Single());

            var req = new HttpRequestMessage(HttpMethod.Get, Url("/status"));
            req.Headers.Host = "evil.example:" + server.Port;
            Assert.Equal(HttpStatusCode.Forbidden, http.SendAsync(req).Result.StatusCode);
        }
    }
}
