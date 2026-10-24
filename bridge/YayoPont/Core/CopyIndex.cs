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

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = lines.Length - 1; i >= 0; i--) // la dernière ligne d'un chemin fait foi
            {
                var parts = lines[i].Split('\t');
                if (parts.Length < 4
                    || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long phoneSize)
                    || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long written)) continue;
                var relative = parts[2];
                if (!seen.Add(relative)) continue;
                var full = Resolve(managedRoot, relative);
                if (full == null) continue;
                try
                {
                    var info = new FileInfo(full);
                    if (!info.Exists || info.Length != written) continue;
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
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
}
