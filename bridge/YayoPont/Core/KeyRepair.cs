using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace YayoPont.Core
{
    /// <summary>Bilan de « Ranger la clé », renvoyé avec l'état de la copie.</summary>
    internal sealed class RepairReport
    {
        public int Duplicates { get; set; }
        public int MotionStripped { get; set; }
        public int LiveRemoved { get; set; }
        public int BrokenRemoved { get; set; }
        public int PartsRemoved { get; set; }
        public long BytesFreed { get; set; }

        /// <summary>Dossiers où un fichier a été supprimé.</summary>
        internal readonly HashSet<string> Touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal bool Changed => Duplicates + MotionStripped + LiveRemoved + BrokenRemoved + PartsRemoved > 0;
    }

    /// <summary>
    /// « Ranger la clé » avant chaque copie, dans le dossier racine géré seulement :
    /// restes de copies interrompues, doublons exacts, photos animées raccourcies,
    /// clips de photos animées, vidéos incomplètes qui vont être recopiées,
    /// dossiers vides. Les copies « Sent » de WhatsApp déjà présentes restent :
    /// rien ne prouve que l'original est sur la clé.
    /// </summary>
    internal static class KeyRepair
    {
        public const double LiveMaxSeconds = 4;
        public const string RepairSuffix = ".yayo-repare";
        private const long MaxPhotoBytes = 256L * 1024 * 1024;

        private sealed class Entry
        {
            public string Path;
            public long Size;
            public string Name => System.IO.Path.GetFileName(Path);
            public string Folder => System.IO.Path.GetDirectoryName(Path);
        }

        /// <param name="stripMotion">raccourcir les photos animées (et retirer leurs clips si <paramref name="removeLive"/>).</param>
        /// <param name="namesInCopy">noms des fichiers du téléphone copiés juste après (vidéos incomplètes à remplacer).</param>
        public static RepairReport Run(string managedRoot, bool stripMotion, bool removeLive, ISet<string> namesInCopy, CancellationToken cancel, bool ownRoot = true)
        {
            var report = new RepairReport();
            if (string.IsNullOrEmpty(managedRoot) || !Directory.Exists(managedRoot)) return report;
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
                            files.Add(new Entry { Path = original, Size = file.Size });
                        }
                    }
                    catch (IOException ex) { Log.Error("rangement : " + file.Path, ex); }
                    files.Remove(file);
                }
            }

            var media = files.Where(f => MediaRules.Kind(f.Name) != null).ToList();

            // 2. Doublons exacts : même taille, puis même SHA-256 complet.
            foreach (var sameSize in media.Where(f => f.Size > 0).GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList())
            {
                var byHash = new Dictionary<string, List<Entry>>();
                foreach (var file in sameSize)
                {
                    cancel.ThrowIfCancellationRequested();
                    var hash = Sha256(file.Path);
                    if (hash == null) continue;
                    if (!byHash.TryGetValue(hash, out var list)) byHash[hash] = list = new List<Entry>();
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
                    foreach (var extra in group.Where(f => f != keep))
                    {
                        if (Delete(root, extra, report))
                        {
                            report.Duplicates++;
                            media.Remove(extra);
                        }
                    }
                }
            }

            // 3. Photos animées : on garde la photo seule, l'index des copies retient le nom et la taille d'origine.
            if (stripMotion)
            {
                var indexed = new HashSet<string>(CopyIndex.Load(root).Select(e => e.RelativePath), StringComparer.OrdinalIgnoreCase);
                foreach (var file in media.Where(f => MotionPhoto.IsJpegName(f.Name)).ToList())
                {
                    cancel.ThrowIfCancellationRequested();
                    if (indexed.Contains(file.Path.Substring(root.Length)) || file.Size > MaxPhotoBytes) continue;
                    if (!MotionPhoto.LooksAnimated(file.Path)) continue;
                    try
                    {
                        if (StripFile(root, file, report)) report.MotionStripped++;
                    }
                    catch (IOException ex) { Log.Error("rangement : photo animée " + file.Path, ex); }
                    catch (UnauthorizedAccessException ex) { Log.Error("rangement : photo animée " + file.Path, ex); }
                }
            }

            // 4. Clips de photos animées : même nom qu'une photo du même dossier, 4 s au plus.
            if (stripMotion && removeLive)
            {
                foreach (var folder in media.GroupBy(f => f.Folder, StringComparer.OrdinalIgnoreCase).ToList())
                {
                    var photos = new HashSet<string>(folder.Where(f => MediaRules.Kind(f.Name) == "photo")
                        .Select(f => Path.GetFileNameWithoutExtension(f.Name)), StringComparer.OrdinalIgnoreCase);
                    if (photos.Count == 0) continue;
                    foreach (var clip in folder.Where(f => Mp4Info.IsMp4Family(f.Name) && photos.Contains(Path.GetFileNameWithoutExtension(f.Name))).ToList())
                    {
                        cancel.ThrowIfCancellationRequested();
                        var seconds = Mp4Info.Duration(clip.Path);
                        if (seconds == null || seconds > LiveMaxSeconds) continue;
                        if (Delete(root, clip, report))
                        {
                            report.LiveRemoved++;
                            media.Remove(clip);
                        }
                    }
                }
            }

            // 5. Vidéos incomplètes que la copie va remplacer.
            if (namesInCopy != null && namesInCopy.Count > 0)
            {
                foreach (var video in media.Where(f => Mp4Info.IsMp4Family(f.Name) && namesInCopy.Contains(f.Name)).ToList())
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

            // 6. Dossiers devenus vides, y compris par une copie annulée (jamais la racine gérée elle-même).
            RemoveEmptyFolders(root, ownRoot ? null : report.Touched);
            return report;
        }

        private static bool StripFile(string root, Entry file, RepairReport report)
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
            File.Delete(file.Path);
            File.Move(temp, file.Path);
            File.SetCreationTime(file.Path, created);
            File.SetLastWriteTime(file.Path, written);
            CopyIndex.Append(root, Destination.WithoutNumber(file.Name), bytes.Length, file.Path, length);
            report.BytesFreed += bytes.Length - length;
            file.Size = length;
            return true;
        }

        private static List<Entry> List(string root)
        {
            var files = new List<Entry>();
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] names, dirs;
                try
                {
                    names = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (var path in names)
                {
                    try { files.Add(new Entry { Path = path, Size = new FileInfo(path).Length }); }
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

        private static bool Delete(string root, Entry file, RepairReport report)
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
