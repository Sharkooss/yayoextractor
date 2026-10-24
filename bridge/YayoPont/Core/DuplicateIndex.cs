using System;
using System.Collections.Generic;
using System.IO;

namespace YayoPont.Core
{
    /// <summary>
    /// Ce qui est déjà sur la destination. Un fichier du téléphone y est « déjà »
    /// si un fichier de la destination a la même taille et soit le même nom, soit
    /// le même début (copie renommée), ou si l'index des copies le connaît (même
    /// nom et même taille d'origine : photo animée raccourcie). Le téléphone n'est
    /// lu que pour les tailles en collision.
    /// </summary>
    internal sealed class DuplicateIndex
    {
        public const int HeadSize = 64 * 1024;

        private readonly Dictionary<long, List<string>> bySize = new Dictionary<long, List<string>>();
        private readonly HashSet<string> copies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();

        public int Count { get; private set; }

        public static DuplicateIndex Build(IEnumerable<(string path, long size)> files)
        {
            var index = new DuplicateIndex();
            foreach (var (path, size) in files) index.Add(path, size);
            return index;
        }

        /// <summary>Index d'une destination : ses médias complets, plus l'index des copies du dossier géré.</summary>
        public static DuplicateIndex ForTarget(Target target)
        {
            var index = Build(MediaFiles(target.IndexRoot));
            foreach (var entry in CopyIndex.Load(target.ManagedRoot)) index.AddCopy(entry.PhoneName, entry.PhoneSize);
            return index;
        }

        public void Add(string path, long size)
        {
            lock (gate)
            {
                if (!bySize.TryGetValue(size, out var paths)) bySize[size] = paths = new List<string>();
                paths.Add(path);
                Count++;
            }
        }

        /// <summary>Un fichier du téléphone (nom, taille d'origine) déjà copié, même si la copie a changé de taille.</summary>
        public void AddCopy(string phoneName, long phoneSize)
        {
            lock (gate) copies.Add(CopyKey(phoneName, phoneSize));
        }

        public bool IsOnDrive(string name, long size, Func<byte[]> phoneHead, Func<string, byte[]> driveHead)
        {
            List<string> candidates;
            lock (gate)
            {
                if (copies.Contains(CopyKey(name, size))) return true;
                if (!bySize.TryGetValue(size, out var paths)) return false;
                candidates = new List<string>(paths);
            }
            foreach (var path in candidates)
            {
                if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            var head = phoneHead();
            if (head == null) return false;
            foreach (var path in candidates)
            {
                if (SameBytes(head, driveHead(path))) return true;
            }
            return false;
        }

        private static string CopyKey(string name, long size) => size + "\t" + name;

        /// <summary>Les 64 premiers Ko d'un fichier (moins s'il est plus petit), null si illisible.</summary>
        public static byte[] ReadHead(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path)) return ReadHead(stream);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static byte[] ReadHead(Stream stream)
        {
            var buffer = new byte[HeadSize];
            int total = 0, read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            if (total == buffer.Length) return buffer;
            var head = new byte[total];
            Array.Copy(buffer, head, total);
            return head;
        }

        /// <summary>
        /// Tous les médias sous un dossier, sans les dossiers système ni ceux qu'on
        /// ne peut pas lire. Une vidéo incomplète n'y figure pas : le fichier du
        /// téléphone redevient « à copier ».
        /// </summary>
        public static IEnumerable<(string path, long size)> MediaFiles(string root)
        {
            foreach (var (path, size) in AllMediaFiles(root))
            {
                if (Mp4Info.IsMp4Family(path) && Mp4Info.IsComplete(path) == false) continue;
                yield return (path, size);
            }
        }

        internal static IEnumerable<(string path, long size)> AllMediaFiles(string root)
        {
            if (!Directory.Exists(root)) yield break;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] files, dirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    dirs = Directory.GetDirectories(dir);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }
                foreach (var file in files)
                {
                    if (MediaRules.Kind(file) == null) continue;
                    long size;
                    try { size = new FileInfo(file).Length; }
                    catch (IOException) { continue; }
                    yield return (file, size);
                }
                foreach (var sub in dirs)
                {
                    if (SystemFolder(sub)) continue;
                    pending.Push(sub);
                }
            }
        }

        /// <summary>Dossiers système d'un lecteur, et jonctions (on ne suit jamais un lien).</summary>
        internal static bool SystemFolder(string path)
        {
            var name = Path.GetFileName(path);
            if (name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)
                || name.Equals("$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)) return true;
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
