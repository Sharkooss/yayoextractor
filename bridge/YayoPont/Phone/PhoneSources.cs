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
        IList<PhoneFile> Enumerate(Action<int> found, CancellationToken cancel);

        Stream OpenRead(string id);

        /// <summary>Vignette JPEG, ou null s'il n'y en a pas.</summary>
        byte[] Thumbnail(string id);
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

    /// <summary>
    /// Mode simulation : un dossier joue le téléphone. Un fichier « VERROUILLE »
    /// à sa racine simule un téléphone verrouillé.
    /// </summary>
    internal sealed class FolderPhoneSource : IPhoneSource
    {
        private readonly string root;

        public FolderPhoneSource(string root)
        {
            this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        }

        public PhoneInfo Detect()
        {
            if (!Directory.Exists(root)) return null;
            return new PhoneInfo
            {
                Name = "Téléphone de test",
                Manufacturer = "Simulation",
                State = File.Exists(Path.Combine(root, "VERROUILLE")) ? "locked" : "ready",
            };
        }

        public IList<PhoneFile> Enumerate(Action<int> found, CancellationToken cancel)
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
                    found(files.Count);
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

        public Stream OpenRead(string id) => File.OpenRead(Resolve(id));

        public byte[] Thumbnail(string id)
        {
            using (var stream = OpenRead(id)) return Thumbnails.FromStream(stream, id);
        }

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
