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
        /// <summary>Le téléphone branché, ou null.</summary>
        PhoneInfo Detect();

        /// <summary>Tous les médias, en appelant <paramref name="found"/> au fil de l'eau.</summary>
        IList<PhoneFile> Enumerate(Action<PhoneFile> found, CancellationToken cancel);

        Stream OpenRead(string id);

        /// <summary>Vignette JPEG, ou null s'il n'y en a pas.</summary>
        byte[] Thumbnail(string id);

        /// <summary>Un téléphone est branché en USB mais n'expose pas ses fichiers (simple recharge).</summary>
        bool UsbSeen();
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
    /// Mode simulation : un dossier joue le téléphone. À sa racine, un fichier
    /// « VERROUILLE » simule un téléphone verrouillé, « RECHARGE » un téléphone
    /// branché en simple recharge.
    /// </summary>
    internal sealed class FolderPhoneSource : IPhoneSource
    {
        private readonly string root;
        private readonly int bytesPerSecond;

        /// <param name="bytesPerSecond">0 = pleine vitesse ; sinon simule le débit d'un vrai téléphone.</param>
        public FolderPhoneSource(string root, int bytesPerSecond = 0)
        {
            this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            this.bytesPerSecond = bytesPerSecond;
        }

        public PhoneInfo Detect()
        {
            if (!Directory.Exists(root) || UsbSeen()) return null;
            return new PhoneInfo
            {
                Name = "Téléphone de test",
                Manufacturer = "Simulation",
                State = File.Exists(Path.Combine(root, "VERROUILLE")) ? "locked" : "ready",
            };
        }

        public IList<PhoneFile> Enumerate(Action<PhoneFile> found, CancellationToken cancel)
        {
            var files = new List<PhoneFile>();
            Walk(root, "", 0);
            return files;

            void Walk(string dir, string relative, int depth)
            {
                cancel.ThrowIfCancellationRequested();
                foreach (var file in Directory.GetFiles(dir))
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
                    });
                    found(files[files.Count - 1]);
                }
                if (depth >= MediaRules.MaxDepth) return;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (MediaRules.SkipFolder(name, relative)) continue;
                    Walk(sub, relative.Length == 0 ? name : relative + "/" + name, depth + 1);
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

        public bool UsbSeen() => File.Exists(Path.Combine(root, "RECHARGE"));

        public void Dispose() { }

        private string Resolve(string id)
        {
            var path = Path.GetFullPath(Path.Combine(root, (id ?? "").Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException("Fichier introuvable sur le téléphone.", id);
            return path;
        }
    }
}
