using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace YayoPont.Core
{
    /// <summary>
    /// Copies WhatsApp « Sent » déjà sur la clé (copiées par le pont 1.0) : retirées
    /// seulement si leur original y est aussi. Une perte de photo est pire qu'un
    /// doublon : sans preuve en deux étapes, on garde.
    /// Candidate : fichier de la clé de même nom et taille qu'un élément masqué d'un
    /// dossier « Sent » du téléphone (et d'aucun élément visible).
    /// Photo : empreinte visuelle d'une photo de la clé datée avant l'envoi (vignette
    /// EXIF d'abord, image entière pour confirmer). Vidéo : même durée à 0,15 s près
    /// (vidéo et son aussi), même format d'image, datée avant l'envoi (30 jours au
    /// plus), plus lourde, puis trois images (25, 50, 75 %, décodées par Media Foundation) qui se ressemblent,
    /// dont deux au moins identiques.
    /// </summary>
    internal static class SentCopies
    {
        public const int HeadBytes = 64 * 1024;
        public static readonly TimeSpan ClockSlack = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan FullDecodeWindow = TimeSpan.FromDays(30);
        public static readonly TimeSpan VideoWindow = TimeSpan.FromDays(30);
        public const double DurationTolerance = 0.15;
        private const int FullDecodeBudget = 300;
        private const long MaxDecodeBytes = 40L * 1024 * 1024;

        private sealed class Original
        {
            public RepairFile File;
            public bool Tried;
            public VisualPrint Coarse;   // vignette EXIF (ou image entière si pas de vignette fiable)
            public bool CoarseIsFull;
            public bool NeedsFull;       // pas de vignette fiable
            public double Aspect;        // cadrage annoncé (EXIF), 0 si inconnu
            public VisualPrint Full;
            public bool FullTried;
        }

        public static List<RepairFile> Redundant(IList<RepairFile> media, IList<PhoneFile> phone, CancellationToken cancel)
        {
            var result = new List<RepairFile>();
            var sent = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in phone)
            {
                if (!file.Hidden) visible.Add(Key(file.Name, file.Size));
                else if (MediaRules.HiddenFolder(file.RelativePath)) sent[Key(file.Name, file.Size)] = file.Date;
            }
            if (sent.Count == 0) return result;
            var candidates = media.Where(f =>
            {
                var key = Key(Destination.WithoutNumber(f.Name), f.Size);
                return sent.ContainsKey(key) && !visible.Contains(key);
            }).ToList();
            if (candidates.Count == 0) return result; // rien à examiner : rien n'est lu

            var excluded = new HashSet<RepairFile>(candidates);
            var photos = media.Where(f => !excluded.Contains(f) && Decodable(f.Name)).Select(f => new Original { File = f }).ToList();
            var videos = media.Where(f => !excluded.Contains(f) && Mp4Info.IsMp4Family(f.Name)).ToList();
            // Copies déjà examinées sans original trouvé, tant que la clé n'a pas changé : rien à relire.
            var signature = media.Count + ":" + media.Sum(f => f.Size) + ":" + media.Max(f => f.Date.Ticks);
            int budget = FullDecodeBudget;
            var videoFacts = new Dictionary<RepairFile, Mp4Info.VideoFacts>();
            var videoFrames = new Dictionary<RepairFile, List<VisualPrint>>();

            foreach (var copy in candidates)
            {
                if (NoOriginal.TryGetValue(copy.Path, out var seen) && seen == signature + ":" + copy.Size) continue;
                cancel.ThrowIfCancellationRequested();
                var sentAt = sent[Key(Destination.WithoutNumber(copy.Name), copy.Size)] ?? copy.Date;
                RepairFile original = null;
                if (Decodable(copy.Name)) original = FindPhoto(copy, sentAt, photos, ref budget, cancel);
                else if (Mp4Info.IsMp4Family(copy.Name)) original = FindVideo(copy, sentAt, videos, videoFacts, videoFrames, cancel);
                if (original == null)
                {
                    NoOriginal[copy.Path] = signature + ":" + copy.Size;
                    continue;
                }
                Log.Info($"rangement : copie WhatsApp « Sent » {copy.Path} retirée, original {original.Path}");
                result.Add(copy);
            }
            return result;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> NoOriginal =
            new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static string Key(string name, long size) => size + "\t" + name;

        /// <summary>Formats que System.Drawing sait lire (pas le HEIC : on garde alors la copie).</summary>
        private static bool Decodable(string name)
        {
            var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".png";
        }

        private static RepairFile FindPhoto(RepairFile copy, DateTime sentAt, List<Original> photos, ref int budget, CancellationToken cancel)
        {
            var print = VisualPrint.FromFile(copy.Path);
            if (print == null) return null;
            // Les plus proches de l'envoi d'abord : on envoie surtout des photos récentes.
            foreach (var original in photos.Where(o => o.File.Date <= sentAt + ClockSlack).OrderByDescending(o => o.File.Date))
            {
                cancel.ThrowIfCancellationRequested();
                Coarse(original);
                if (original.Aspect > 0 && !VisualPrint.SameAspect(original.Aspect, print.Aspect, 0.03)) continue;
                if (original.NeedsFull && !original.CoarseIsFull)
                {
                    // Pas de vignette fiable : image entière, seulement près de l'envoi et dans un budget.
                    if (original.File.Date < sentAt - FullDecodeWindow || original.File.Size > MaxDecodeBytes) continue;
                    if (!original.FullTried)
                    {
                        if (budget <= 0) continue;
                        budget--;
                    }
                    original.Coarse = FullPrint(original);
                    original.CoarseIsFull = true;
                }
                // Étape 1 : vignette (ou image entière) ressemblante.
                if (original.Coarse == null || !original.Coarse.Resembles(print)) continue;
                // Étape 2 : image entière, comparaison fine.
                if (print.Confirms(FullPrint(original))) return original.File;
            }
            return null;
        }

        /// <summary>Empreinte grossière depuis la vignette EXIF des 64 premiers Ko, si son cadrage est celui de la photo.</summary>
        private static void Coarse(Original original)
        {
            if (original.Tried) return;
            original.Tried = true;
            original.NeedsFull = true;
            if (!MotionPhoto.IsJpegName(original.File.Name)) return;
            byte[] head;
            try
            {
                using (var stream = new FileStream(original.File.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                    head = DuplicateIndex.ReadHead(stream);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            var exif = ExifInfo.Read(head);
            int width = exif.PixelWidth, height = exif.PixelHeight;
            if (width <= 0 || height <= 0) JpegSize(head, out width, out height);
            bool swap = exif.Orientation >= 5;
            if (width > 0 && height > 0) original.Aspect = swap ? height / (double)width : width / (double)height;
            if (exif.ThumbOffset < 0 || original.Aspect <= 0) return;
            var thumb = VisualPrint.FromBytes(head, exif.ThumbOffset, exif.ThumbLength, exif.Orientation);
            // Vignette au cadrage différent (bandes noires, recadrée) : inutilisable.
            if (thumb == null || !VisualPrint.SameAspect(thumb.Aspect, original.Aspect, 0.02)) return;
            original.Coarse = thumb;
            original.NeedsFull = false;
        }

        private static VisualPrint FullPrint(Original original)
        {
            if (!original.FullTried)
            {
                original.FullTried = true;
                original.Full = VisualPrint.FromFile(original.File.Path);
            }
            return original.Full;
        }

        /// <summary>Dimensions du marqueur SOF, s'il est dans la tête lue.</summary>
        private static void JpegSize(byte[] b, out int width, out int height)
        {
            width = height = 0;
            if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return;
            int i = 2;
            while (i + 9 < b.Length && b[i] == 0xFF)
            {
                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xDA || marker == 0xD9) return;
                int length = (b[i + 2] << 8) | b[i + 3];
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    height = (b[i + 5] << 8) | b[i + 6];
                    width = (b[i + 7] << 8) | b[i + 8];
                    return;
                }
                if (length < 2) return;
                i += 2 + length;
            }
        }

        private static RepairFile FindVideo(RepairFile copy, DateTime sentAt, List<RepairFile> videos, Dictionary<RepairFile, Mp4Info.VideoFacts> cache,
            Dictionary<RepairFile, List<VisualPrint>> frames, CancellationToken cancel)
        {
            if (Mp4Info.IsComplete(copy.Path) != true) return null;
            var facts = Mp4Info.Facts(Mp4Info.ReadMoov(copy.Path));
            if (facts?.Duration == null || facts.Width <= 0 || facts.Height <= 0) return null;
            var matches = new List<RepairFile>();
            foreach (var video in videos)
            {
                cancel.ThrowIfCancellationRequested();
                if (video.Size <= copy.Size || video.Date > sentAt + ClockSlack || video.Date < sentAt - VideoWindow) continue;
                if (!cache.TryGetValue(video, out var other))
                {
                    other = Mp4Info.IsComplete(video.Path) == true ? Mp4Info.Facts(Mp4Info.ReadMoov(video.Path)) : null;
                    cache[video] = other;
                }
                if (other?.Duration == null || other.Width <= 0 || other.Height <= 0) continue;
                // Étape 1 : durée et format d'image.
                if (Math.Abs(other.Duration.Value - facts.Duration.Value) > DurationTolerance) continue;
                if (!VisualPrint.SameAspect(other.Width / (double)other.Height, facts.Width / (double)facts.Height, 0.03)) continue;
                // Chaque piste a la même durée (une piste son présente des deux côtés ou d'aucun).
                if (!Close(other.VideoDuration, facts.VideoDuration) || !Close(other.AudioDuration, facts.AudioDuration)) continue;
                matches.Add(video);
            }
            if (matches.Count == 0) return null;

            // Étape 2 : la durée ne prouve rien (sur de vrais clips de 2-3 s, des vidéos différentes
            // ont la même) : trois images (25, 50, 75 %) de la copie et de l'original doivent être
            // les mêmes. Vidéo non décodable (HEVC sans son extension, Windows « N ») : on garde.
            var mine = VideoFrames.Prints(copy.Path, facts.Duration.Value, facts.Rotation, Fractions);
            if (mine == null) return null;
            foreach (var video in matches)
            {
                cancel.ThrowIfCancellationRequested();
                if (!frames.TryGetValue(video, out var theirs))
                {
                    var other = cache[video];
                    frames[video] = theirs = VideoFrames.Prints(video.Path, other.Duration.Value, other.Rotation, Fractions);
                }
                if (SameVideo(mine, theirs)) return video;
            }
            return null;
        }

        private static readonly double[] Fractions = { 0.25, 0.5, 0.75 };

        /// <summary>
        /// Les trois images se ressemblent (dHash) et au moins deux se confirment : une image
        /// peut tomber à une image près d'écart entre l'original et la copie réencodée.
        /// </summary>
        internal static bool SameVideo(List<VisualPrint> a, List<VisualPrint> b) =>
            a != null && b != null && a.Count == b.Count && a.Count > 0
            && a.Select((p, k) => p.Resembles(b[k])).All(ok => ok)
            && a.Select((p, k) => p.Confirms(b[k])).Count(ok => ok) >= 2;

        private static bool Close(double? a, double? b) =>
            a.HasValue == b.HasValue && (!a.HasValue || Math.Abs(a.Value - b.Value) <= DurationTolerance);
    }
}
