using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace YayoPont.Core
{
    /// <summary>Bilan de « Ranger la clé », renvoyé avec l'état de l'analyse et de la copie.</summary>
    internal sealed class RepairReport
    {
        public int Duplicates { get; set; }
        public int MotionStripped { get; set; }
        public int LiveRemoved { get; set; }
        public int BrokenRemoved { get; set; }
        public int PartsRemoved { get; set; }
        public int SentRemoved { get; set; }
        public long BytesFreed { get; set; }

        /// <summary>Dossiers où un fichier a été supprimé.</summary>
        internal readonly HashSet<string> Touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool Changed => Duplicates + MotionStripped + LiveRemoved + BrokenRemoved + PartsRemoved + SentRemoved > 0;
    }

    /// <summary>Ce que le rangement sait du téléphone et de ce qu'on lui demande.</summary>
    internal sealed class RepairContext
    {
        /// <summary>Raccourcir les photos animées (et retirer leurs clips si <see cref="RemoveLive"/>).</summary>
        public bool StripMotion = true;
        public bool RemoveLive = true;

        /// <summary>Le dossier géré est à nous (sinon seuls les dossiers vidés par le rangement sont retirés).</summary>
        public bool OwnRoot = true;

        /// <summary>Noms des fichiers du téléphone recopiés ensuite : leurs vidéos incomplètes sur la clé sont retirées.</summary>
        public ISet<string> BrokenNames;

        /// <summary>La liste du téléphone (analyse), ou null.</summary>
        public IList<PhoneFile> Phone;
    }

    /// <summary>Un fichier du dossier géré.</summary>
    internal sealed class RepairFile
    {
        public string Path;
        public long Size;
        public DateTime Date;
        public string Name => System.IO.Path.GetFileName(Path);
        public string Folder => System.IO.Path.GetDirectoryName(Path);
    }

    /// <summary>
    /// « Ranger la clé », dans le dossier racine géré seulement, au début de
    /// l'analyse et de la copie : restes de copies interrompues, photos animées
    /// raccourcies, doublons exacts, clips de photos animées, vidéos incomplètes
    /// qui vont être recopiées, copies WhatsApp « Sent » dont l'original est sur
    /// la clé, dossiers vides.
    /// </summary>
    internal static class KeyRepair
    {
        public const double LiveMaxSeconds = 4;
        public const long LiveMaxBytes = 12L * 1024 * 1024;
        public static readonly TimeSpan LiveMaxGap = TimeSpan.FromMinutes(2);
        public const string RepairSuffix = ".yayo-repare";
        private const long MaxPhotoBytes = 256L * 1024 * 1024;

        /// <summary>Forme 1.1.0, gardée pour les appels simples.</summary>
        public static RepairReport Run(string managedRoot, bool stripMotion, bool removeLive, ISet<string> namesInCopy, CancellationToken cancel, bool ownRoot = true) =>
            Run(managedRoot, new RepairContext { StripMotion = stripMotion, RemoveLive = removeLive, BrokenNames = namesInCopy, OwnRoot = ownRoot }, cancel);

        public static RepairReport Run(string managedRoot, RepairContext context, CancellationToken cancel)
        {
            var report = new RepairReport();
            if (string.IsNullOrEmpty(managedRoot) || !Directory.Exists(managedRoot)) return report;
            context = context ?? new RepairContext();
            var root = Path.GetFullPath(managedRoot).TrimEnd('\\') + "\\";
            var files = List(root);

            // 1. Restes de copies interrompues ; un raccourcissement interrompu est remis en place.
            foreach (var file in files.ToList())
            {
                cancel.ThrowIfCancellationRequested();
                if (file.Path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    && MediaRules.Kind(file.Path.Substring(0, file.Path.Length - 5)) != null)
                {
                    if (Delete(root, file, report)) report.PartsRemoved++;
                    files.Remove(file);
                }
                else if (file.Path.EndsWith(RepairSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    var original = file.Path.Substring(0, file.Path.Length - RepairSuffix.Length);
                    try
                    {
                        if (File.Exists(original)) { if (Delete(root, file, report)) report.PartsRemoved++; }
                        else
                        {
                            File.Move(file.Path, original);
                            files.Add(new RepairFile { Path = original, Size = file.Size, Date = file.Date });
                        }
                    }
                    catch (IOException ex) { Log.Error("rangement : " + file.Path, ex); }
                    files.Remove(file);
                }
            }

            var media = files.Where(f => MediaRules.Kind(f.Name) != null).ToList();

            // 2. Photos animées, avant les doublons : deux copies d'une même photo animée,
            //    l'une déjà raccourcie, deviennent identiques. L'index des copies retient
            //    le nom et la taille d'origine.
            if (context.StripMotion)
            {
                var stripped = new HashSet<string>(CopyIndex.Load(root).Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);
                var seen = SeenPhotos.Load(root);
                bool dirty = false;
                void Seen(string path, RepairFile f) { seen[path] = (f.Size, f.Date.Ticks); dirty = true; }
                foreach (var file in media.Where(f => MotionPhoto.CanStrip(f.Name)).ToList())
                {
                    cancel.ThrowIfCancellationRequested();
                    if (stripped.Contains(file.Path.Substring(root.Length)) || file.Size > MaxPhotoBytes) continue;
                    var relative = file.Path.Substring(root.Length);
                    if (seen.TryGetValue(relative, out var was) && was == (file.Size, file.Date.Ticks)) continue;
                    if (!MotionPhoto.LooksAnimated(file.Path))
                    {
                        Seen(relative, file);
                        continue;
                    }
                    try
                    {
                        if (StripFile(root, file, report)) report.MotionStripped++;
                        else Seen(relative, file); // lue en entier : pas de vidéo
                    }
                    catch (IOException ex) { Log.Error("rangement : photo animée " + file.Path, ex); }
                    catch (UnauthorizedAccessException ex) { Log.Error("rangement : photo animée " + file.Path, ex); }
                }
                if (dirty) SeenPhotos.Save(root, seen, media.Select(f => f.Path.Substring(root.Length)));
            }

            // 3. Doublons exacts : même taille, puis même SHA-256 complet.
            RemoveDuplicates(root, media, report, cancel);

            // 4. Clips de photos animées.
            if (context.StripMotion && context.RemoveLive) RemoveClips(root, media, context.Phone, report, cancel);

            // 5. Vidéos incomplètes que la copie va remplacer.
            var broken = context.BrokenNames;
            if (broken != null && broken.Count > 0)
            {
                foreach (var video in media.Where(f => Mp4Info.IsMp4Family(f.Name) && broken.Contains(f.Name)).ToList())
                {
                    cancel.ThrowIfCancellationRequested();
                    if (Mp4Info.IsComplete(video.Path) != false) continue;
                    if (Delete(root, video, report))
                    {
                        report.BrokenRemoved++;
                        media.Remove(video);
                    }
                }
            }

            // 6. Copies WhatsApp « Sent » dont l'original est sur la clé.
            if (context.Phone != null)
            {
                foreach (var sent in SentCopies.Redundant(media, context.Phone, cancel))
                {
                    if (Delete(root, sent, report))
                    {
                        report.SentRemoved++;
                        media.Remove(sent);
                    }
                }
            }

            // 7. Dossiers devenus vides, y compris par une copie annulée (jamais la racine gérée elle-même).
            RemoveEmptyFolders(root, context.OwnRoot ? null : report.Touched);
            if (report.Changed)
            {
                Log.Info($"rangement de {root} : {report.MotionStripped} photos animées raccourcies, {report.Duplicates} doublons, "
                    + $"{report.LiveRemoved} clips, {report.BrokenRemoved} vidéos incomplètes, {report.SentRemoved} copies « Sent », "
                    + $"{report.PartsRemoved} restes, {report.BytesFreed} octets libérés");
            }
            return report;
        }

        private static void RemoveDuplicates(string root, List<RepairFile> media, RepairReport report, CancellationToken cancel)
        {
            List<CopyIndex.Entry> index = null;
            foreach (var sameSize in media.Where(f => f.Size > 0).GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList())
            {
                var byHash = new Dictionary<string, List<RepairFile>>();
                foreach (var file in sameSize)
                {
                    cancel.ThrowIfCancellationRequested();
                    var hash = Sha256(file.Path);
                    if (hash == null) continue;
                    if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<RepairFile>();
                    list.Add(file);
                }
                foreach (var group in byHash.Values.Where(l => l.Count > 1))
                {
                    var keep = group
                        .OrderBy(f => Destination.HasNumber(f.Name))
                        .ThenBy(f => f.Path.Length)
                        .ThenBy(f => CreationTime(f.Path))
                        .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                        .First();
                    index = index ?? CopyIndex.Load(root);
                    var keptEntries = new HashSet<string>(index.Where(e => Same(root, e, keep)).Select(e => e.PhoneSize + "\t" + e.PhoneName), StringComparer.OrdinalIgnoreCase);
                    foreach (var extra in group.Where(f => f != keep))
                    {
                        // Ce que l'index des copies savait du fichier retiré passe au fichier gardé.
                        var moved = index.Where(e => Same(root, e, extra)).ToList();
                        if (!Delete(root, extra, report)) continue;
                        report.Duplicates++;
                        media.Remove(extra);
                        foreach (var entry in moved)
                        {
                            if (keptEntries.Add(entry.PhoneSize + "\t" + entry.PhoneName))
                                CopyIndex.Append(root, entry.PhoneName, entry.PhoneSize, keep.Path, keep.Size);
                        }
                    }
                }
            }
        }

        private static bool Same(string root, CopyIndex.Entry entry, RepairFile file) =>
            string.Equals(entry.RelativePath, file.Path.Substring(root.Length), StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Clips de photos animées (4 s au plus, 12 Mo au plus) : même nom qu'une photo
        /// du même dossier, ou qu'un clip repéré sur le téléphone, ou qu'une photo de la
        /// clé datée à moins de 2 min (clip rangé dans le mois voisin), ou clip iPhone
        /// (métadonnées QuickTime) dont la photo est sur la clé ou sur le téléphone.
        /// </summary>
        private static void RemoveClips(string root, List<RepairFile> media, IList<PhoneFile> phone, RepairReport report, CancellationToken cancel)
        {
            var photos = media.Where(f => MediaRules.Kind(f.Name) == "photo").ToList();
            if (photos.Count == 0 && phone == null) return;
            var byStem = photos.ToLookup(f => Stem(f.Name), StringComparer.OrdinalIgnoreCase);
            var phoneLive = new HashSet<string>(phone?.Where(f => f.Live).Select(f => f.Name) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var phonePhotos = new HashSet<string>(phone?.Where(f => f.Kind == "photo").Select(f => Stem(f.Name)) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            foreach (var clip in media.Where(f => Mp4Info.IsMp4Family(f.Name) && f.Size <= LiveMaxBytes).ToList())
            {
                cancel.ThrowIfCancellationRequested();
                var stem = Stem(clip.Name);
                var keyPhotos = byStem[stem].ToList();
                bool sameFolder = keyPhotos.Any(p => string.Equals(p.Folder, clip.Folder, StringComparison.OrdinalIgnoreCase));
                bool onPhone = phoneLive.Contains(Destination.WithoutNumber(clip.Name));
                bool near = keyPhotos.Any(p => (p.Date - clip.Date).Duration() < LiveMaxGap);
                bool somewhere = keyPhotos.Count > 0 || phonePhotos.Contains(stem);
                if (!sameFolder && !onPhone && !near && !somewhere) continue; // rien à lire
                var facts = Mp4Info.Facts(Mp4Info.ReadMoov(clip.Path));
                if (facts?.Duration == null || facts.Duration > LiveMaxSeconds) continue;
                if (!sameFolder && !onPhone && !near && !(facts.AppleLive && somewhere)) continue;
                if (Delete(root, clip, report))
                {
                    report.LiveRemoved++;
                    media.Remove(clip);
                }
            }
        }

        /// <summary>
        /// Nom commun d'une photo et de son clip : sans extension ni « (n) », et
        /// « IMG_E1234 » (retouchée sur iPhone) comme « IMG_1234 ».
        /// </summary>
        internal static string Stem(string name)
        {
            var stem = Path.GetFileNameWithoutExtension(Destination.WithoutNumber(name ?? ""));
            if (stem.Length > 5 && stem.StartsWith("IMG_E", StringComparison.OrdinalIgnoreCase) && char.IsDigit(stem[5]))
                stem = "IMG_" + stem.Substring(5);
            return stem;
        }

        private static bool StripFile(string root, RepairFile file, RepairReport report)
        {
            var bytes = File.ReadAllBytes(file.Path);
            int length = MotionPhoto.Strip(bytes, bytes.Length);
            if (length >= bytes.Length) return false;
            var created = File.GetCreationTime(file.Path);
            var written = File.GetLastWriteTime(file.Path);
            // Écriture à côté puis remplacement : interrompu, l'étape 1 remet la photo en place.
            var temp = file.Path + RepairSuffix;
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes, 0, length);
                output.Flush(true);
            }
            File.SetAttributes(file.Path, FileAttributes.Normal);
            File.Delete(file.Path);
            File.Move(temp, file.Path);
            File.SetCreationTime(file.Path, created);
            File.SetLastWriteTime(file.Path, written);
            CopyIndex.Append(root, Destination.WithoutNumber(file.Name), bytes.Length, file.Path, length);
            report.BytesFreed += bytes.Length - length;
            file.Size = length;
            return true;
        }

        private static List<RepairFile> List(string root)
        {
            var files = new List<RepairFile>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                FileInfo[] infos;
                string[] dirs;
                try
                {
                    infos = new DirectoryInfo(dir).GetFiles();
                    dirs = Directory.GetDirectories(dir);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var info in infos)
                {
                    try { files.Add(new RepairFile { Path = info.FullName, Size = info.Length, Date = info.LastWriteTime }); }
                    catch (IOException) { }
                }
                foreach (var sub in dirs)
                {
                    if (Skip(sub)) continue;
                    pending.Push(sub);
                }
            }
            return files;
        }

        /// <summary>Dossiers système, cachés (« .Trash-1000 »…) et liens : jamais touchés.</summary>
        private static bool Skip(string dir) =>
            DuplicateIndex.SystemFolder(dir) || Path.GetFileName(dir).StartsWith(".");

        /// <param name="only">null : tous les dossiers vides ; sinon seulement ceux-là (et leurs parents), quand la racine gérée est toute la destination.</param>
        private static void RemoveEmptyFolders(string root, ISet<string> only)
        {
            foreach (var dir in SubFolders(root).OrderByDescending(d => d.Length))
            {
                if (only != null && !only.Any(t => Target.Inside(t, dir))) continue;
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static IEnumerable<string> SubFolders(string root)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string[] dirs;
                try { dirs = Directory.GetDirectories(pending.Pop()); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var dir in dirs)
                {
                    if (Skip(dir)) continue;
                    result.Add(dir);
                    pending.Push(dir);
                }
            }
            return result;
        }

        private static bool Delete(string root, RepairFile file, RepairReport report)
        {
            var full = Path.GetFullPath(file.Path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false; // jamais hors du dossier géré
            try
            {
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
                report.BytesFreed += file.Size;
                report.Touched.Add(Path.GetDirectoryName(full));
                return true;
            }
            catch (IOException ex) { Log.Error("rangement : suppression de " + full, ex); }
            catch (UnauthorizedAccessException ex) { Log.Error("rangement : suppression de " + full, ex); }
            return false;
        }

        internal static string Sha256(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024))
                    return Convert.ToBase64String(sha.ComputeHash(stream));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static DateTime CreationTime(string path)
        {
            try { return File.GetCreationTime(path); }
            catch (IOException) { return DateTime.MaxValue; }
        }
    }
}
