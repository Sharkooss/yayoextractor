using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using YayoPont.Core;
using YayoPont.Drives;
using YayoPont.Http;
using YayoPont.Jobs;
using YayoPont.Phone;

namespace YayoPont
{
    /// <summary>Les routes du pont (voir la spec, section « API du pont »).</summary>
    internal sealed class Api
    {
        public const int Version = 1;

        private readonly Security security;
        private readonly IPhoneSource phone;
        private readonly IDriveProvider drives;
        private readonly string appVersion;
        private readonly ConcurrentDictionary<string, ScanJob> scans = new ConcurrentDictionary<string, ScanJob>();
        private readonly ConcurrentDictionary<string, TransferJob> transfers = new ConcurrentDictionary<string, TransferJob>();
        private readonly object gate = new object();

        private ScanJob currentScan;
        private TransferJob currentTransfer;
        private PhoneInfo lastPhone;

        public Api(Security security, IPhoneSource phone, IDriveProvider drives, string appVersion)
        {
            this.security = security;
            this.phone = phone;
            this.drives = drives;
            this.appVersion = appVersion;
        }

        public HttpResponse Handle(HttpRequest req)
        {
            if (req.Method == "OPTIONS") return security.Preflight(req);
            var res = Route(req);
            security.Decorate(req, res);
            return res;
        }

        private HttpResponse Route(HttpRequest req)
        {
            var path = req.Path.Length > 1 ? req.Path.TrimEnd('/') : req.Path;

            if (req.Method == "GET" && path == "/status")
            {
                return security.Gate(req, false) ?? HttpResponse.Json(new Dictionary<string, object>
                {
                    ["app"] = "yayo-pont",
                    ["version"] = appVersion,
                    ["api"] = Version,
                });
            }
            if (req.Method == "POST" && path == "/session")
            {
                var denied = security.Gate(req, false);
                if (denied != null) return denied;
                if (!Security.OriginAllowed(req.Header("Origin"))) return HttpResponse.Error(403, "Origine requise.");
                return HttpResponse.Json(new Dictionary<string, object> { ["token"] = security.Token });
            }

            var refused = security.Gate(req, true);
            if (refused != null) return refused;

            if (req.Method == "GET" && path == "/devices") return Devices();
            if (req.Method == "POST" && path == "/scan") return StartScan(req);
            if (req.Method == "GET" && path.StartsWith("/scan/")) return ScanStatus(path.Substring(6));
            if (req.Method == "GET" && path.StartsWith("/thumb/")) return Thumb(path.Substring(7));
            if (req.Method == "GET" && path.StartsWith("/media/")) return Media(path.Substring(7));
            if (req.Method == "POST" && path == "/transfer") return StartTransfer(req);
            if (req.Method == "POST" && path.StartsWith("/transfer/") && path.EndsWith("/cancel"))
                return CancelTransfer(path.Substring(10, path.Length - 10 - 7));
            if (req.Method == "GET" && path.StartsWith("/transfer/")) return TransferStatus(path.Substring(10));
            if (req.Method == "POST" && path == "/open") return Open(req);
            if (req.Method == "POST" && path == "/eject") return Eject(req);
            return HttpResponse.Error(404, "Route inconnue.");
        }

        private HttpResponse Devices()
        {
            PhoneInfo info;
            // Pendant une analyse, le thread WPD est occupé : on renvoie le dernier état connu.
            if (currentScan != null && currentScan.State == "running") info = lastPhone;
            else
            {
                try { info = phone.Detect(); }
                catch (Exception ex)
                {
                    Log.Error("détection du téléphone", ex);
                    info = null;
                }
                lastPhone = info;
            }
            IList<DriveDto> list;
            try { list = drives.List(); }
            catch (Exception ex)
            {
                Log.Error("liste des clés", ex);
                list = new List<DriveDto>();
            }
            return HttpResponse.Json(new Dictionary<string, object>
            {
                ["phone"] = info,
                ["usbPhoneSeen"] = info == null && phone.UsbSeen(),
                ["drives"] = list,
            });
        }

        private HttpResponse StartScan(HttpRequest req)
        {
            var drive = DriveFromBody(req, out var error);
            if (drive == null) return error;
            lock (gate)
            {
                if (currentScan != null && currentScan.State == "running") return JobId(currentScan.Id);
                if (currentTransfer != null && currentTransfer.State == "running") return HttpResponse.Error(409, "Une copie est en cours.");
                var job = new ScanJob();
                scans[job.Id] = job;
                currentScan = job;
                Background(() => job.Run(phone, drive, CancellationToken.None));
                return JobId(job.Id);
            }
        }

        private HttpResponse ScanStatus(string id)
        {
            if (!scans.TryGetValue(id, out var job)) return HttpResponse.Error(404, "Analyse inconnue.");
            var body = new Dictionary<string, object>
            {
                ["id"] = job.Id,
                ["state"] = job.State,
                ["phase"] = job.Phase,
                ["photos"] = job.Photos,
                ["videos"] = job.Videos,
                ["onDrive"] = job.OnDriveCount,
                ["error"] = job.Error,
            };
            if (job.State == "done") body["items"] = job.Items;
            return HttpResponse.Json(body);
        }

        private HttpResponse Thumb(string id)
        {
            try
            {
                var jpeg = phone.Thumbnail(id);
                if (jpeg == null) return HttpResponse.Error(404, "Pas de vignette.");
                var res = HttpResponse.Bytes(jpeg, "image/jpeg");
                res.Headers["Cache-Control"] = "private, max-age=86400";
                return res;
            }
            catch (IOException) { return HttpResponse.Error(404, "Pas de vignette."); }
        }

        private HttpResponse Media(string id)
        {
            try
            {
                // Les identifiants WPD n'ont pas d'extension : le type vient du nom du fichier.
                PhoneFile file = null;
                currentScan?.Files.TryGetValue(id, out file);
                return new HttpResponse { ContentType = ContentType(file?.Name ?? id), Stream = phone.OpenRead(id) };
            }
            catch (IOException ex) { return HttpResponse.Error(404, ex.Message); }
        }

        private HttpResponse StartTransfer(HttpRequest req)
        {
            var drive = DriveFromBody(req, out var error);
            if (drive == null) return error;
            var ids = (Body(req)?.TryGetValue("ids", out var raw) == true ? raw as List<object> : null)?.OfType<string>().ToList();
            if (ids == null || ids.Count == 0) return HttpResponse.Error(400, "Aucune photo choisie.");
            lock (gate)
            {
                var scan = currentScan;
                if (scan == null || scan.State != "done" || !string.Equals(scan.Drive.Id, drive.Id, StringComparison.OrdinalIgnoreCase))
                    return HttpResponse.Error(409, "Il faut d'abord analyser le téléphone avec cette clé.");
                if (currentTransfer != null && currentTransfer.State == "running") return JobId(currentTransfer.Id);
                var files = ids.Where(scan.Files.ContainsKey).Select(id => scan.Files[id]).ToList();
                var job = new TransferJob();
                transfers[job.Id] = job;
                currentTransfer = job;
                Background(() => job.Run(phone, drive, files, scan.Index));
                return JobId(job.Id);
            }
        }

        private HttpResponse TransferStatus(string id) =>
            transfers.TryGetValue(id, out var job) ? HttpResponse.Json(job) : HttpResponse.Error(404, "Copie inconnue.");

        private HttpResponse CancelTransfer(string id)
        {
            if (!transfers.TryGetValue(id, out var job)) return HttpResponse.Error(404, "Copie inconnue.");
            job.Cancel();
            return HttpResponse.Json(new Dictionary<string, object> { ["ok"] = true });
        }

        private HttpResponse Open(HttpRequest req)
        {
            var drive = DriveFromBody(req, out var error);
            if (drive == null) return error;
            DriveActions.Open(drive);
            return HttpResponse.Json(new Dictionary<string, object> { ["ok"] = true });
        }

        private HttpResponse Eject(HttpRequest req)
        {
            var drive = DriveFromBody(req, out var error);
            if (drive == null) return error;
            if (currentTransfer != null && currentTransfer.State == "running") return HttpResponse.Error(409, "Attends la fin de la copie.");
            bool ok = drives.Eject(drive.Id, out var message);
            return HttpResponse.Json(new Dictionary<string, object> { ["ok"] = ok, ["message"] = message });
        }

        private DriveDto DriveFromBody(HttpRequest req, out HttpResponse error)
        {
            error = null;
            var id = Body(req)?.TryGetValue("driveId", out var raw) == true ? raw as string : null;
            var drive = id == null ? null : drives.Find(id);
            if (drive == null) error = HttpResponse.Error(404, "Clé introuvable : est-elle toujours branchée ?");
            return drive;
        }

        private static Dictionary<string, object> Body(HttpRequest req)
        {
            try { return Json.Parse(req.BodyText) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static HttpResponse JobId(string id) => HttpResponse.Json(new Dictionary<string, object> { ["jobId"] = id });

        private static void Background(Action work) => new Thread(() => work()) { IsBackground = true }.Start();

        private static string ContentType(string name)
        {
            switch (Path.GetExtension(name).ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".heic":
                case ".heif": return "image/heic";
                case ".mp4":
                case ".m4v": return "video/mp4";
                case ".mov": return "video/quicktime";
                case ".webm": return "video/webm";
                default: return "application/octet-stream";
            }
        }
    }
}
