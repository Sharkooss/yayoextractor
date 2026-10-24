using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    /// <summary>Ignoré sauf si la variable d'environnement YAYO_VRAIS_FICHIERS désigne un dossier.</summary>
    public sealed class RealSamplesFactAttribute : FactAttribute
    {
        public RealSamplesFactAttribute()
        {
            var dir = Environment.GetEnvironmentVariable("YAYO_VRAIS_FICHIERS");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Skip = "YAYO_VRAIS_FICHIERS non défini : vrais fichiers non testés.";
        }
    }

    /// <summary>
    /// Vrais fichiers de téléphones (non versionnés : licences et taille). Chaque photo
    /// du dossier passe dans Strip ; la sortie est écrite dans YAYO_VRAIS_FICHIERS_SORTIE
    /// (par défaut « dossier_sortie ») avec un bilan TSV, pour contrôle par ffprobe.
    /// </summary>
    public sealed class RealSamplesTests
    {
        /// <summary>Dossier des bilans : YAYO_VRAIS_FICHIERS_SORTIE, sinon « dossier_sortie ».</summary>
        private static string Output()
        {
            var output = Environment.GetEnvironmentVariable("YAYO_VRAIS_FICHIERS_SORTIE")
                ?? Environment.GetEnvironmentVariable("YAYO_VRAIS_FICHIERS").TrimEnd('\\', '/') + "_sortie";
            Directory.CreateDirectory(output);
            return output;
        }

        [RealSamplesFact]
        public void Vrais_fichiers_passes_dans_strip()
        {
            var dir = Environment.GetEnvironmentVariable("YAYO_VRAIS_FICHIERS");
            var output = Output();
            var invalid = new List<string>();
            var report = new StringBuilder("fichier\ttaille\tdetection\tsortie\tvideo_retiree\tmp4_debut\tremorque\r\n");
            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var relative = path.Substring(dir.TrimEnd('\\', '/').Length + 1);
                if (!MotionPhoto.CanStrip(path) && !Mp4Info.IsMp4Family(path)) continue;
                var bytes = File.ReadAllBytes(path);
                if (Mp4Info.IsMp4Family(path))
                {
                    var facts = Mp4Info.Facts(Mp4Info.ReadMoov(path));
                    report.Append(relative).Append('\t').Append(bytes.Length).Append("\tvideo\t")
                        .Append(facts?.Duration?.ToString("0.000", CultureInfo.InvariantCulture)).Append('\t')
                        .Append(facts?.Width).Append('x').Append(facts?.Height).Append('\t')
                        .Append(facts != null && facts.AppleLive ? "live-photo" : "").Append("\t\r\n");
                    continue;
                }
                bool looks = MotionPhoto.LooksAnimated(path);
                int videoStart = MotionPhoto.IsJpegName(path) ? MotionPhoto.VideoStart(bytes, bytes.Length) : -1;
                var copy = (byte[])bytes.Clone();
                int length = MotionPhoto.Strip(copy, copy.Length);
                var target = Path.Combine(output, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(target, copy.Take(length).ToArray());
                // Jamais un fichier invalide : JPEG commence par FFD8 et finit par l'image principale (FFD9).
                if (length < bytes.Length)
                {
                    bool valid = bytes[0] == 0xFF
                        ? copy[0] == 0xFF && copy[1] == 0xD8 && MotionPhoto.ImageEnd(copy, length) > 0
                        : MotionPhoto.HeifEnd(copy, length, out var mpvd) == length && !mpvd;
                    if (!valid) invalid.Add(relative);
                }
                report.Append(relative).Append('\t').Append(bytes.Length).Append('\t').Append(looks ? "animee" : "fixe").Append('\t')
                    .Append(length).Append('\t').Append(bytes.Length - length).Append('\t').Append(videoStart).Append('\t')
                    .Append(Encoding.ASCII.GetString(bytes, Math.Max(0, bytes.Length - 4), Math.Min(4, bytes.Length)).Replace("\0", ".")).Append("\r\n");
            }
            File.WriteAllText(Path.Combine(output, "bilan.tsv"), report.ToString(), Encoding.UTF8);
            Assert.Empty(invalid);
        }

        /// <summary>
        /// Copies « Sent » fabriquées comme WhatsApp à partir des vraies photos (orientation
        /// appliquée, 1600 px au plus, JPEG qualité 70, sans EXIF). Une photo sur deux garde
        /// son original sur la clé : sa copie doit partir ; l'autre moitié n'a plus
        /// d'original : sa copie doit rester (aucune autre vraie photo ne doit la « reconnaître »).
        /// </summary>
        [RealSamplesFact]
        public void Vraies_photos_copies_sent()
        {
            var dir = Environment.GetEnvironmentVariable("YAYO_VRAIS_FICHIERS");
            var key = Path.Combine(Path.GetTempPath(), "yayo-vraies-sent-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(key, "Photos du téléphone");
            var log = new StringBuilder();
            try
            {
                var phone = new List<PhoneFile>();
                var kept = new List<string>();
                var orphans = new List<string>();
                var seen = new HashSet<string>();
                int i = 0;
                foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(p => MotionPhoto.IsJpegName(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    byte[] sent;
                    try { sent = WhatsAppCopy(path); }
                    catch (Exception ex) when (ex is ArgumentException || ex is System.Runtime.InteropServices.ExternalException) { log.AppendLine("illisible : " + path); continue; }
                    // Deux échantillons identiques une fois réduits (photo animée et sa version fixe) : un seul.
                    if (!seen.Add(Convert.ToBase64String(System.Security.Cryptography.SHA256.Create().ComputeHash(sent)))) { log.AppendLine("même copie qu'un autre : " + path); continue; }
                    var taken = new DateTime(2025, 10, 1).AddHours(i);
                    var sentAt = taken.AddDays(1);
                    var name = $"IMG-20251002-WA{i:0000}.jpg";
                    bool withOriginal = i % 2 == 0;
                    if (withOriginal)
                    {
                        var original = Path.Combine(root, "2025", "10 - Octobre", $"orig_{i:000}_" + Path.GetFileName(path));
                        Directory.CreateDirectory(Path.GetDirectoryName(original));
                        File.Copy(path, original);
                        File.SetLastWriteTime(original, taken);
                    }
                    var copy = Path.Combine(root, "2025", "10 - Octobre", name);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy));
                    File.WriteAllBytes(copy, sent);
                    File.SetLastWriteTime(copy, sentAt);
                    phone.Add(RepairV2Tests.Phone("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/" + name, sent.Length, sentAt, hidden: true));
                    (withOriginal ? kept : orphans).Add(copy);
                    log.AppendLine($"{name}\t{(withOriginal ? "original sur la clé" : "sans original")}\t{Path.GetFileName(path)}");
                    i++;
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var report = KeyRepair.Run(root, new RepairContext { Phone = phone }, System.Threading.CancellationToken.None);
                watch.Stop();
                var removed = kept.Where(p => !File.Exists(p)).ToList();
                var falsePositives = orphans.Where(p => !File.Exists(p)).ToList();
                log.AppendLine($"copies avec original : {kept.Count}, retirées : {removed.Count} ; sans original : {orphans.Count}, retirées à tort : {falsePositives.Count} ; {watch.ElapsedMilliseconds} ms");
                foreach (var miss in kept.Where(File.Exists)) log.AppendLine("gardée malgré l'original : " + Path.GetFileName(miss));
                File.WriteAllText(Path.Combine(Output(), "sent.txt"), log.ToString(), Encoding.UTF8);
                Assert.Empty(falsePositives);
                Assert.Equal(removed.Count, report.SentRemoved);
                Assert.True(removed.Count >= kept.Count * 9 / 10, log.ToString());
            }
            finally
            {
                if (Directory.Exists(key)) Directory.Delete(key, true);
            }
        }

        /// <summary>
        /// Vidéos : YAYO_VRAIES_VIDEOS contient « orig » (vraies vidéos, dont celles des photos
        /// animées) et « sent » (les mêmes réencodées comme WhatsApp, par ffmpeg). Une sur deux
        /// garde son original sur la clé. Aucune copie sans original ne doit partir.
        /// </summary>
        [RealSamplesFact]
        public void Vraies_videos_copies_sent()
        {
            var videos = Environment.GetEnvironmentVariable("YAYO_VRAIES_VIDEOS");
            if (string.IsNullOrEmpty(videos)) return;
            var key = Path.Combine(Path.GetTempPath(), "yayo-vraies-videos-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(key, "Photos du téléphone");
            var log = new StringBuilder();
            try
            {
                var phone = new List<PhoneFile>();
                var kept = new List<string>();
                var orphans = new List<string>();
                int i = 0;
                foreach (var original in Directory.GetFiles(Path.Combine(videos, "orig")).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var sentSource = Path.Combine(videos, "sent", Path.GetFileNameWithoutExtension(original) + ".mp4");
                    if (!File.Exists(sentSource)) continue;
                    var taken = new DateTime(2025, 10, 1).AddHours(i);
                    var sentAt = taken.AddDays(1);
                    bool withOriginal = i % 2 == 0;
                    var folder = Path.Combine(root, "2025", "10 - Octobre");
                    Directory.CreateDirectory(folder);
                    if (withOriginal)
                    {
                        var target = Path.Combine(folder, $"orig_{i:000}_" + Path.GetFileName(original));
                        File.Copy(original, target);
                        File.SetLastWriteTime(target, taken);
                    }
                    var name = $"VID-20251002-WA{i:0000}.mp4";
                    var copy = Path.Combine(folder, name);
                    File.Copy(sentSource, copy);
                    File.SetLastWriteTime(copy, sentAt);
                    phone.Add(RepairV2Tests.Phone("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/Sent/" + name, new FileInfo(copy).Length, sentAt, hidden: true));
                    (withOriginal ? kept : orphans).Add(copy);
                    var a = Mp4Info.Facts(Mp4Info.ReadMoov(original));
                    var b = Mp4Info.Facts(Mp4Info.ReadMoov(copy));
                    log.AppendLine($"{name}\t{(withOriginal ? "original" : "sans original")}\t{Path.GetFileName(original)}\t"
                        + $"{a?.Duration:0.000}s {a?.Width}x{a?.Height} son {a?.AudioDuration:0.000} -> {b?.Duration:0.000}s {b?.Width}x{b?.Height} son {b?.AudioDuration:0.000}");
                    i++;
                }
                var report = KeyRepair.Run(root, new RepairContext { Phone = phone }, System.Threading.CancellationToken.None);
                var removed = kept.Where(p => !File.Exists(p)).ToList();
                var falsePositives = orphans.Where(p => !File.Exists(p)).ToList();
                log.AppendLine($"copies avec original : {kept.Count}, retirées : {removed.Count} ; sans original : {orphans.Count}, retirées à tort : {falsePositives.Count}");
                foreach (var miss in kept.Where(File.Exists)) log.AppendLine("gardée malgré l'original : " + Path.GetFileName(miss));
                File.WriteAllText(Path.Combine(Output(), "sent-videos.txt"), log.ToString(), Encoding.UTF8);
                Assert.Empty(falsePositives);
                Assert.Equal(removed.Count, report.SentRemoved);
            }
            finally
            {
                if (Directory.Exists(key)) Directory.Delete(key, true);
            }
        }

        /// <summary>Ce que fait WhatsApp d'une photo envoyée : orientation appliquée, 1600 px au plus, JPEG recompressé sans EXIF.</summary>
        private static byte[] WhatsAppCopy(string path)
        {
            using (var image = System.Drawing.Image.FromFile(path))
            {
                int orientation = 1;
                if (image.PropertyIdList.Contains(0x0112)) orientation = image.GetPropertyItem(0x0112).Value[0];
                using (var bitmap = new System.Drawing.Bitmap(image))
                {
                    var rotations = new Dictionary<int, System.Drawing.RotateFlipType>
                    {
                        [2] = System.Drawing.RotateFlipType.RotateNoneFlipX, [3] = System.Drawing.RotateFlipType.Rotate180FlipNone,
                        [4] = System.Drawing.RotateFlipType.RotateNoneFlipY, [5] = System.Drawing.RotateFlipType.Rotate90FlipX,
                        [6] = System.Drawing.RotateFlipType.Rotate90FlipNone, [7] = System.Drawing.RotateFlipType.Rotate270FlipX,
                        [8] = System.Drawing.RotateFlipType.Rotate270FlipNone,
                    };
                    if (rotations.TryGetValue(orientation, out var flip)) bitmap.RotateFlip(flip);
                    double scale = Math.Min(1, 1600.0 / Math.Max(bitmap.Width, bitmap.Height));
                    using (var small = MediaFactory.Resized(bitmap, Math.Max(1, (int)(bitmap.Width * scale)), Math.Max(1, (int)(bitmap.Height * scale))))
                        return MediaFactory.Encode(small, 70);
                }
            }
        }

        /// <summary>
        /// Une « clé » faite des vrais fichiers, rangée par KeyRepair : chaque photo animée
        /// raccourcie (comme Strip seul), les clips iPhone retirés même rangés dans le mois
        /// voisin ou une autre année, tout le reste intact octet pour octet.
        /// </summary>
        [RealSamplesFact]
        public void Vraie_cle_rangee()
        {
            var dir = Environment.GetEnvironmentVariable("YAYO_VRAIS_FICHIERS");
            var key = Path.Combine(Path.GetTempPath(), "yayo-vraie-cle-" + Guid.NewGuid().ToString("N"));
            var root = Path.Combine(key, "Photos du téléphone");
            try
            {
                var originals = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                var expectedStripped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var expectedRemoved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var photoStems = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(p => MediaRules.Kind(p) == "photo")
                    .Select(p => Path.GetFileNameWithoutExtension(p)).ToList();
                int n = 0;
                foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories).OrderBy(p => MediaRules.Kind(p) == "photo" ? 0 : 1))
                {
                    var name = Path.GetFileName(path);
                    if (MediaRules.Kind(name) == null) continue;
                    var bytes = File.ReadAllBytes(path);
                    var stem = Path.GetFileNameWithoutExtension(name);
                    bool clip = Mp4Info.IsMp4Family(name) && photoStems.Contains(stem, StringComparer.OrdinalIgnoreCase);
                    // Photos dans « 2025/10 », leurs clips : la moitié dans le mois voisin (20 s plus tard), l'autre moitié une autre année.
                    var date = new DateTime(2025, 10, 31, 23, 59, 30).AddMinutes(-10 * n++);
                    string folder = Path.Combine("2025", "10 - Octobre");
                    if (clip)
                    {
                        var photo = Path.Combine(root, folder, Path.GetFileName(Directory.GetFiles(dir, stem + ".*", SearchOption.AllDirectories).First(p => MediaRules.Kind(p) == "photo")));
                        if (n % 2 == 0) { folder = Path.Combine("2025", "11 - Novembre"); date = File.GetLastWriteTime(photo).AddSeconds(20); }
                        else { folder = Path.Combine("2023", "01 - Janvier"); date = new DateTime(2023, 1, 5); }
                    }
                    var target = Path.Combine(root, folder, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, bytes);
                    File.SetLastWriteTime(target, date);
                    originals[target] = bytes;
                    var copy = (byte[])bytes.Clone();
                    if (MotionPhoto.CanStrip(name) && MotionPhoto.Strip(copy, copy.Length) < bytes.Length) expectedStripped.Add(target);
                    // Clip : 4 s au plus, et (même dossier, ou moins de 2 min, ou métadonnées Live Photo).
                    var facts = Mp4Info.IsMp4Family(name) ? Mp4Info.Facts(Mp4Info.ReadMoov(target)) : null;
                    if (clip && facts?.Duration <= 4 && (folder.StartsWith("2025") || facts.AppleLive)) expectedRemoved.Add(target);
                }
                var report = KeyRepair.Run(root, new RepairContext { Phone = new List<PhoneFile>() }, System.Threading.CancellationToken.None);

                Assert.Equal(expectedStripped.Count, report.MotionStripped);
                Assert.Equal(expectedRemoved.Count, report.LiveRemoved);
                Assert.Equal(0, report.Duplicates + report.SentRemoved + report.BrokenRemoved);
                foreach (var pair in originals)
                {
                    if (expectedRemoved.Contains(pair.Key)) Assert.False(File.Exists(pair.Key), pair.Key);
                    else if (expectedStripped.Contains(pair.Key))
                    {
                        var copy = (byte[])pair.Value.Clone();
                        int length = MotionPhoto.Strip(copy, copy.Length);
                        Assert.Equal(copy.Take(length).ToArray(), File.ReadAllBytes(pair.Key));
                    }
                    else Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
                }
                // Second passage : plus rien.
                Assert.False(KeyRepair.Run(root, new RepairContext { Phone = new List<PhoneFile>() }, System.Threading.CancellationToken.None).Changed);
                File.WriteAllText(Path.Combine(Output(), "cle.txt"),
                    $"photos raccourcies {report.MotionStripped}, clips retirés {report.LiveRemoved} : "
                    + string.Join(", ", expectedRemoved.Select(Path.GetFileName)) + "\r\n", Encoding.UTF8);
            }
            finally
            {
                if (Directory.Exists(key))
                {
                    foreach (var file in Directory.GetFiles(key, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(key, true);
                }
            }
        }
    }
}
