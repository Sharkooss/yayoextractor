using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace YayoPont.Core
{
    /// <summary>
    /// Index des copies, « .yayo-pont.tsv » (caché, système) à la racine du dossier
    /// géré : une ligne par fichier copié ou réparé,
    /// nom sur le téléphone, taille sur le téléphone, chemin relatif, taille écrite.
    /// Il reconnaît une photo animée déjà copiée, donc raccourcie : son nom et sa
    /// taille d'origine ne se retrouvent plus sur la clé.
    /// </summary>
    internal static class CopyIndex
    {
        public const string FileName = ".yayo-pont.tsv";
        private static readonly object Gate = new object();

        internal struct Entry
        {
            public string PhoneName;
            public long PhoneSize;
            public string RelativePath;
            public long WrittenSize;
        }

        public static string PathFor(string managedRoot) => Path.Combine(managedRoot, FileName);

        /// <summary>Entrées dont le fichier existe encore avec la taille écrite.</summary>
        public static List<Entry> Load(string managedRoot)
        {
            var entries = new List<Entry>();
            var path = PathFor(managedRoot);
            string[] lines;
            try
            {
                if (!File.Exists(path)) return entries;
                lock (Gate) lines = File.ReadAllLines(path, Encoding.UTF8);
            }
            catch (IOException) { return entries; }
            catch (UnauthorizedAccessException) { return entries; }

            // Toutes les lignes valides comptent : un doublon retiré lègue ses entrées au fichier gardé.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sizes = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var parts = lines[i].Split('\t');
                if (parts.Length < 4
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long phoneSize)
                    || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long written)) continue;
                var relative = parts[2];
                if (!seen.Add(lines[i])) continue;
                if (!sizes.TryGetValue(relative, out var size)) sizes[relative] = size = SizeOf(Resolve(managedRoot, relative));
                if (size != written) continue;
                entries.Add(new Entry { PhoneName = parts[0], PhoneSize = phoneSize, RelativePath = relative, WrittenSize = written });
            }
            return entries;
        }

        public static void Append(string managedRoot, string phoneName, long phoneSize, string fullPath, long writtenSize)
        {
            var root = managedRoot.TrimEnd('\\') + "\\";
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            var line = string.Join("\t", Clean(phoneName), phoneSize.ToString(CultureInfo.InvariantCulture),
                Clean(fullPath.Substring(root.Length)), writtenSize.ToString(CultureInfo.InvariantCulture)) + "\r\n";
            var path = PathFor(managedRoot);
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(managedRoot);
                    bool created = !File.Exists(path);
                    // FileMode.Append : ouvrir en « création » un fichier caché échouerait.
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        var bytes = Encoding.UTF8.GetBytes(line);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    if (created) File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);
                }
            }
            catch (IOException ex) { Log.Error("index des copies", ex); }
            catch (UnauthorizedAccessException ex) { Log.Error("index des copies", ex); }
        }

        private static long? SizeOf(string full)
        {
            if (full == null) return null;
            try
            {
                var info = new FileInfo(full);
                return info.Exists ? info.Length : (long?)null;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        private static string Resolve(string managedRoot, string relative)
        {
            try
            {
                var root = Path.GetFullPath(managedRoot).TrimEnd('\\') + "\\";
                var full = Path.GetFullPath(Path.Combine(root, relative));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }
    }

    /// <summary>
    /// Photos de la clé déjà examinées et sans vidéo, « .yayo-pont-vues.tsv » (caché,
    /// système) : chemin relatif, taille, date (ticks). Sur une clé USB 2 de 14 000
    /// photos, chaque analyse ne rouvre ainsi que les photos nouvelles ou modifiées.
    /// </summary>
    internal static class SeenPhotos
    {
        public const string FileName = ".yayo-pont-vues.tsv";

        public static Dictionary<string, (long size, long ticks)> Load(string managedRoot)
        {
            var seen = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = Path.Combine(managedRoot, FileName);
                if (!File.Exists(path)) return seen;
                foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var parts = line.Split('\t');
                    if (parts.Length == 3
                        && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size)
                        && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
                        seen[parts[0]] = (size, ticks);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return seen;
        }

        /// <summary>Réécrit la liste, limitée aux fichiers encore présents.</summary>
        public static void Save(string managedRoot, Dictionary<string, (long size, long ticks)> seen, IEnumerable<string> present)
        {
            var keep = new HashSet<string>(present, StringComparer.OrdinalIgnoreCase);
            var text = new StringBuilder();
            foreach (var pair in seen)
            {
                if (!keep.Contains(pair.Key) || pair.Key.IndexOf('\t') >= 0) continue;
                text.Append(pair.Key).Append('\t').Append(pair.Value.size.ToString(CultureInfo.InvariantCulture))
                    .Append('\t').Append(pair.Value.ticks.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            var path = Path.Combine(managedRoot, FileName);
            try
            {
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); // un fichier caché ne se réécrit pas
                File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
                File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.System);
            }
            catch (IOException ex) { Log.Error("liste des photos examinées", ex); }
            catch (UnauthorizedAccessException ex) { Log.Error("liste des photos examinées", ex); }
        }
    }
}
