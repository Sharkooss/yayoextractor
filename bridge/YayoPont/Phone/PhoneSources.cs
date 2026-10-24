using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using YayoPont.Core;
using YayoPont.Drives;

namespace YayoPont.Phone
{
    internal interface IPhoneSource : IDisposable
    {
        /// <summary>Tous les téléphones branchés (liste vide s'il n'y en a pas).</summary>
        IList<PhoneInfo> Phones();

        /// <summary>
        /// Tous les médias du téléphone <paramref name="phoneId"/> (null = le premier),
        /// en appelant <paramref name="found"/> au fil de l'eau. Ce téléphone devient
        /// celui des lectures suivantes (OpenRead, Thumbnail).
        /// </summary>
        IList<PhoneFile> Enumerate(string phoneId, Action<PhoneFile> found, CancellationToken cancel);

        Stream OpenRead(string id);

        /// <summary>Vignette JPEG, ou null s'il n'y en a pas.</summary>
        byte[] Thumbnail(string id);

        /// <summary>Un téléphone est branché en USB mais n'expose pas ses fichiers (simple recharge).</summary>
        bool UsbSeen();
    }

    internal static class PhoneSourceExtensions
    {
        /// <summary>Le premier téléphone branché, ou null.</summary>
        public static PhoneInfo Detect(this IPhoneSource source) => source.Phones().FirstOrDefault();

        public static IList<PhoneFile> Enumerate(this IPhoneSource source, Action<PhoneFile> found, CancellationToken cancel) =>
            source.Enumerate(null, found, cancel);
    }

    /// <summary>
    /// Ce que la Galerie montre d'un dossier : on regarde ses fichiers avant de
    /// classer ses médias, car un « .nomedia » cache tout le dossier et ses
    /// sous-dossiers (WPD et le système de fichiers ne le listent pas en premier).
    /// </summary>
    internal static class GalleryRules
    {
        public static bool FolderHidden(bool parentHidden, string relativePath, IEnumerable<string> fileNames) =>
            parentHidden || MediaRules.HiddenFolder(relativePath) || fileNames.Any(MediaRules.IsNoMedia);
    }

    /// <summary>Un appareil USB d'un fabricant de téléphones est branché (même en simple recharge).</summary>
    internal static class PhoneUsb
    {
        private static readonly HashSet<int> PhoneVendors = new HashSet<int>
        {
            0x04E8, // Samsung
            0x18D1, // Google
            0x2717, // Xiaomi
            0x12D1, // Huawei
            0x2A70, // OnePlus
            0x22D9, // Oppo, Realme
            0x2D95, // Vivo
            0x22B8, // Motorola
            0x1004, // LG
            0x0FCE, // Sony
            0x2E04, // HMD (Nokia)
        };

        public static bool IsPhone(int vid, int pid) =>
            PhoneVendors.Contains(vid) || (vid == 0x05AC && pid >= 0x1290 && pid <= 0x12AF); // iPhone/iPad

        public static bool Seen()
        {
            try { return Native.PresentUsbDevices().Any(d => IsPhone(d.vid, d.pid)); }
            catch (Exception ex) { Log.Error("liste USB", ex); return false; }
        }
    }

    /// <summary>Lecture bridée, pour voir l'animation de copie en mode simulation.</summary>
    internal sealed class SlowStream : Stream
    {
        private readonly Stream inner;
        private readonly int bytesPerSecond;

        public SlowStream(Stream inner, int bytesPerSecond)
        {
            this.inner = inner;
            this.bytesPerSecond = bytesPerSecond;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, Math.Min(count, 64 * 1024));
            if (read > 0) Thread.Sleep((int)(1000L * read / bytesPerSecond));
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Mode simulation : un dossier joue le téléphone (plusieurs dossiers =
    /// plusieurs téléphones). À sa racine, un fichier « VERROUILLE » simule un
    /// téléphone verrouillé, « RECHARGE » un téléphone branché en simple recharge.
    /// </summary>
    internal sealed class FolderPhoneSource : IPhoneSource
    {
        private readonly string[] roots;
        private readonly int bytesPerSecond;
        private volatile string root;

        /// <param name="bytesPerSecond">0 = pleine vitesse ; sinon simule le débit d'un vrai téléphone.</param>
        public FolderPhoneSource(string root, int bytesPerSecond = 0) : this(new[] { root }, bytesPerSecond) { }

        public FolderPhoneSource(IEnumerable<string> roots, int bytesPerSecond = 0)
        {
            this.roots = roots.Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar)).ToArray();
            if (this.roots.Length == 0) throw new ArgumentException("Au moins un dossier de téléphone simulé.");
            this.bytesPerSecond = bytesPerSecond;
            root = this.roots[0];
        }

        private static string PhoneId(int index) => "test-" + (index + 1);

        public IList<PhoneInfo> Phones()
        {
            var phones = new List<PhoneInfo>();
            for (int i = 0; i < roots.Length; i++)
            {
                var dir = roots[i];
                if (!Directory.Exists(dir) || File.Exists(Path.Combine(dir, "RECHARGE"))) continue;
                phones.Add(new PhoneInfo
                {
                    Id = PhoneId(i),
                    Name = roots.Length == 1 ? "Téléphone de test" : $"Téléphone de test {i + 1} ({Path.GetFileName(dir)})",
                    Manufacturer = "Simulation",
                    State = File.Exists(Path.Combine(dir, "VERROUILLE")) ? "locked" : "ready",
                });
            }
            return phones;
        }

        public IList<PhoneFile> Enumerate(string phoneId, Action<PhoneFile> found, CancellationToken cancel)
        {
            if (phoneId != null)
            {
                int index = -1;
                for (int i = 0; i < roots.Length; i++) if (PhoneId(i) == phoneId) index = i;
                if (index < 0 || !Directory.Exists(roots[index])) throw new IOException("Ce téléphone n'est plus branché.");
                root = roots[index];
            }
            else
            {
                root = roots.FirstOrDefault(Directory.Exists) ?? throw new IOException("Aucun téléphone branché.");
            }
            var files = new List<PhoneFile>();
            Walk(root, "", 0, false);
            return files;

            void Walk(string dir, string relative, int depth, bool parentHidden)
            {
                cancel.ThrowIfCancellationRequested();
                var children = Directory.GetFiles(dir);
                bool hidden = GalleryRules.FolderHidden(parentHidden, relative, children.Select(Path.GetFileName));
                foreach (var file in children)
                {
                    var name = Path.GetFileName(file);
                    var kind = MediaRules.Kind(name);
                    if (kind == null) continue;
                    var rel = relative.Length == 0 ? name : relative + "/" + name;
                    var info = new FileInfo(file);
                    files.Add(new PhoneFile
                    {
                        Id = rel,
                        Name = name,
                        RelativePath = rel,
                        Size = info.Length,
                        Date = info.LastWriteTime,
                        Kind = kind,
                        Source = MediaRules.Source(rel),
                        Hidden = hidden || MediaRules.HiddenFile(name),
                    });
                    found(files[files.Count - 1]);
                }
                if (depth >= MediaRules.MaxDepth) return;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (MediaRules.SkipFolder(name, relative)) continue;
                    Walk(sub, relative.Length == 0 ? name : relative + "/" + name, depth + 1, hidden);
                }
            }
        }

        public Stream OpenRead(string id)
        {
            Stream stream = File.OpenRead(Resolve(id));
            return bytesPerSecond > 0 ? new SlowStream(stream, bytesPerSecond) : stream;
        }

        public byte[] Thumbnail(string id)
        {
            using (var stream = OpenRead(id)) return Thumbnails.FromStream(stream, id);
        }

        public bool UsbSeen() => roots.Any(r => File.Exists(Path.Combine(r, "RECHARGE")));

        public void Dispose() { }

        private string Resolve(string id)
        {
            var root = this.root;
            var path = Path.GetFullPath(Path.Combine(root, (id ?? "").Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException("Fichier introuvable sur le téléphone.", id);
            return path;
        }
    }
}
