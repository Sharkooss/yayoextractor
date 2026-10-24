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
using YayoPont.Setup;

namespace YayoPont
{
    /// <summary>Les routes du pont (voir les specs, sections « API du pont » et « API du pont v2 »).</summary>
    internal sealed class Api
    {
        public const int Version = 2;

        public static readonly string[] Features = { "phones", "folders", "layout", "deep", "repair", "update", "pick-folder" };

        private readonly Security security;
        private readonly IPhoneSource phone;
        private readonly IDriveProvider drives;
        private readonly string appVersion;
        private readonly ConcurrentDictionary<string, ScanJob> scans = new ConcurrentDictionary<string, ScanJob>();
        private readonly ConcurrentDictionary<string, TransferJob> transfers = new ConcurrentDictionary<string, TransferJob>();
        private readonly object gate = new object();

        private ScanJob currentScan;
        private TransferJob currentTransfer;
        private IList<PhoneInfo> lastPhones = new List<PhoneInfo>();
        private string selectedPhoneId;
        private int picking;

        public Api(Security security, IPhoneSource phone, IDriveProvider drives, string appVersion)
        {
            this.security = security;
            this.phone = phone;
            this.drives = drives;
            this.appVersion = appVersion;
        }

        /// <summary>Sélecteur de dossier (remplacé par un faux dans les tests).</summary>
        public IFolderPicker Picker { get; set; } = new WindowsFolderPicker();

        /// <summary>Mise à jour automatique, null si elle n'est pas branchée.</summary>
        public Updater Updater { get; set; }

        /// <summary>Une analyse ou une copie tourne : pas de mise à jour maintenant.</summary>
        public bool Busy => currentScan?.State == "running" || currentTransfer?.State == "running";

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
                    ["features"] = Features,
                    ["updating"] = Updater?.Updating ?? false,
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

            if (req.Method == "GET" && path == "/devices") return Devices(req);
            if (req.Method == "POST" && path == "/scan") return StartScan(req);
            if (req.Method == "GET" && path.StartsWith("/scan/")) return ScanStatus(path.Substring(6));
            if (req.Method == "GET" && path.StartsWith("/thumb/")) return Thumb(path.Substring(7));
            if (req.Method == "GET" && path.StartsWith("/media/")) return Media(path.Substring(7));
            if (req.Method == "POST" && path == "/plan") return Plan(req);
            if (req.Method == "POST" && path == "/transfer") return StartTransfer(req);
            if (req.Method == "POST" && path.StartsWith("/transfer/") && path.EndsWith("/cancel"))
                return CancelTransfer(path.Substring(10, path.Length - 10 - 7));
            if (req.Method == "GET" && path.StartsWith("/transfer/")) return TransferStatus(path.Substring(10));
            if (req.Method == "POST" && path == "/open") return Open(req);
            if (req.Method == "POST" && path == "/eject") return Eject(req);
            if (req.Method == "POST" && path == "/pick-folder") return PickFolder();
            if (req.Method == "POST" && path == "/update") return Update();
            return HttpResponse.Error(404, "Route inconnue.");
        }

        private HttpResponse Devices(HttpRequest req)
        {
            IList<PhoneInfo> phones;
            // Pendant une analyse, le thread WPD est occupé : on renvoie le dernier état connu.
            if (currentScan != null && currentScan.State == "running") phones = lastPhones;
            else
            {
                try { phones = phone.Phones(); }
                catch (Exception ex)
                {
                    Log.Error("détection du téléphone", ex);
                    phones = new List<PhoneInfo>();
                }
                lastPhones = phones;
            }
            var wanted = req.QueryValue("phoneId") ?? selectedPhoneId;
            var chosen = phones.FirstOrDefault(p => p.Id == wanted) ?? phones.FirstOrDefault();
            IList<DriveDto> list;
            try { list = drives.List(req.QueryValue("all") == "1"); }
            catch (Exception ex)
            {
                Log.Error("liste des clés", ex);
                list = new List<DriveDto>();
            }
            return HttpResponse.Json(new Dictionary<string, object>
            {
                ["phone"] = chosen,
                ["phones"] = phones,
                ["usbPhoneSeen"] = phones.Count == 0 && phone.UsbSeen(),
                ["drives"] = list,
            });
        }

        private HttpResponse StartScan(HttpRequest req)
        {
            var body = Body(req);
            var target = TargetFromBody(body, out var error);
            if (target == null) return error;
            var phoneId = Text(body, "phoneId");
            var scanOptions = Section(body, "options");
            var options = new ScanOptions
            {
                Deep = Flag(scanOptions, "deep") ?? false,
                Repair = Flag(scanOptions, "repair") ?? true,
                StripMotion = Flag(scanOptions, "stripMotion") ?? true,
            };
            lock (gate)
            {
                if (currentScan != null && currentScan.State == "running") return JobId(currentScan.Id);
                if (currentTransfer != null && currentTransfer.State == "running") return HttpResponse.Error(409, "Une copie est en cours.");
                var job = new ScanJob();
                scans[job.Id] = job;
                currentScan = job;
                selectedPhoneId = phoneId;
                Background(() => job.Run(phone, phoneId, target, options, CancellationToken.None));
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
                ["hidden"] = job.Hidden,
                ["live"] = job.Live,
                ["duplicates"] = job.Duplicates,
                ["detailsDone"] = job.DetailsDone,
                ["detailsTotal"] = job.DetailsTotal,
                ["repair"] = job.Repair,
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

        /// <summary>Ce qu'une copie demande : destination analysée, fichiers choisis, options.</summary>
        private HttpResponse CopyRequest(HttpRequest req, out Target target, out ScanJob scan, out List<PhoneFile> files, out TransferOptions options)
        {
            scan = null;
            files = null;
            options = null;
            var body = Body(req);
            target = TargetFromBody(body, out var error);
            if (target == null) return error;
            var ids = (body != null && body.TryGetValue("ids", out var raw) ? raw as List<object> : null)?.OfType<string>().ToList();
            if (ids == null || ids.Count == 0) return HttpResponse.Error(400, "Aucune photo choisie.");
            options = ParseOptions(Section(body, "options"));
            scan = currentScan;
            if (scan == null || scan.State != "done" || scan.Target.Key != target.Key)
                return HttpResponse.Error(409, target.Drive != null
                    ? "Il faut d'abord analyser le téléphone avec cette clé."
                    : "Il faut d'abord analyser le téléphone avec ce dossier.");
            var known = scan.Files;
            files = ids.Where(known.ContainsKey).Select(id => known[id]).ToList();
            return null;
        }

        private HttpResponse Plan(HttpRequest req)
        {
            var refused = CopyRequest(req, out var target, out _, out var files, out var options);
            return refused ?? HttpResponse.Json(TransferPlan.Build(target, files, options));
        }

        private HttpResponse StartTransfer(HttpRequest req)
        {
            lock (gate)
            {
                var refused = CopyRequest(req, out var target, out var scan, out var files, out var options);
                if (refused != null) return refused;
                if (currentTransfer != null && currentTransfer.State == "running") return JobId(currentTransfer.Id);
                // Autre dossier racine que celui de l'analyse : l'index des doublons est refait.
                var index = string.Equals(scan.Target.ManagedRoot, target.ManagedRoot, StringComparison.OrdinalIgnoreCase) ? scan.Index : null;
                var job = new TransferJob();
                transfers[job.Id] = job;
                currentTransfer = job;
                var phoneFiles = scan.Files.Values.ToList();
                Background(() => job.Run(phone, target, files, index, options, phoneFiles));
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
            var target = TargetFromBody(Body(req), out var error);
            if (target == null) return error;
            DriveActions.Open(target);
            return HttpResponse.Json(new Dictionary<string, object> { ["ok"] = true });
        }

        private HttpResponse Eject(HttpRequest req)
        {
            var id = Text(Body(req), "driveId");
            var drive = id == null ? null : drives.Find(id);
            if (drive == null) return HttpResponse.Error(404, "Clé introuvable : est-elle toujours branchée ?");
            if (currentTransfer != null && currentTransfer.State == "running") return HttpResponse.Error(409, "Attends la fin de la copie.");
            bool ok = drives.Eject(drive.Id, out var message);
            return HttpResponse.Json(new Dictionary<string, object> { ["ok"] = ok, ["message"] = message });
        }

        /// <summary>La requête attend la réponse de Louis (le site patiente jusqu'à 10 min) ; un seul sélecteur à la fois.</summary>
        private HttpResponse PickFolder()
        {
            if (Interlocked.CompareExchange(ref picking, 1, 0) != 0) return HttpResponse.Error(409, "Le sélecteur de dossier est déjà ouvert.");
            try
            {
                var path = Picker.Pick();
                return HttpResponse.Json(path == null
                    ? new Dictionary<string, object> { ["cancelled"] = true }
                    : new Dictionary<string, object> { ["path"] = path });
            }
            catch (Exception ex)
            {
                Log.Error("sélecteur de dossier", ex);
                return HttpResponse.Error(500, "Impossible d'ouvrir le sélecteur de dossier.");
            }
            finally
            {
                Volatile.Write(ref picking, 0);
            }
        }

        private HttpResponse Update()
        {
            if (Updater == null)
                return HttpResponse.Json(new Dictionary<string, object> { ["state"] = "error", ["error"] = "Mise à jour automatique indisponible." });
            return HttpResponse.Json(Updater.Check());
        }

        /// <summary>{driveId} : une clé ou un disque ; {folder} : un dossier du PC. rootName à la racine du corps ou dans options.</summary>
        private Target TargetFromBody(Dictionary<string, object> body, out HttpResponse error)
        {
            error = null;
            var rootName = Text(body, "rootName") ?? Text(Section(body, "options"), "rootName");
            var folder = Text(body, "folder");
            if (folder != null)
            {
                var target = Target.ForFolder(folder, rootName, out var message);
                if (target == null) error = HttpResponse.Error(400, message);
                return target;
            }
            var id = Text(body, "driveId");
            var drive = id == null ? null : drives.Find(id);
            if (drive == null)
            {
                error = HttpResponse.Error(404, "Clé introuvable : est-elle toujours branchée ?");
                return null;
            }
            return Target.ForDrive(drive, rootName);
        }

        internal static TransferOptions ParseOptions(Dictionary<string, object> o)
        {
            var options = new TransferOptions();
            if (o == null) return options;
            if (Text(o, "layout") is string layout) options.Layout = layout;
            if (Text(o, "fileName") is string fileName && fileName.Trim().Length > 0) options.FileName = fileName;
            options.Places = Section(o, "places");
            options.StripMotion = Flag(o, "stripMotion") ?? options.StripMotion;
            options.Repair = Flag(o, "repair") ?? options.Repair;
            options.Dedupe = Flag(o, "dedupe") ?? options.Dedupe;
            options.Verify = Flag(o, "verify") ?? options.Verify;
            options.KeepDates = Flag(o, "keepDates") ?? options.KeepDates;
            var conflict = Text(o, "conflict");
            if (conflict == "skip" || conflict == "overwrite" || conflict == "rename") options.Conflict = conflict;
            return options;
        }

        private static Dictionary<string, object> Body(HttpRequest req)
        {
            try { return Json.Parse(req.BodyText) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            catch (IndexOutOfRangeException) { return null; }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        private static Dictionary<string, object> Section(Dictionary<string, object> body, string key) =>
            body != null && body.TryGetValue(key, out var value) ? value as Dictionary<string, object> : null;

        private static string Text(Dictionary<string, object> body, string key) =>
            body != null && body.TryGetValue(key, out var value) ? value as string : null;

        private static bool? Flag(Dictionary<string, object> body, string key) =>
            body != null && body.TryGetValue(key, out var value) && value is bool b ? b : (bool?)null;

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
