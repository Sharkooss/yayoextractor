using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace YayoPont.Core
{
    /// <summary>
    /// Lecture minimale des vidéos MP4/MOV/M4V/3GP (conteneur ISO-BMFF) :
    /// complétude, durée (mvhd) et lieu (udta/©xyz, ISO 6709).
    /// </summary>
    internal static class Mp4Info
    {
        private const int MaxMoovBytes = 16 * 1024 * 1024;

        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".m4v", ".3gp",
        };

        // Boîtes qu'on s'attend à trouver en tête d'un vrai fichier : sinon ce n'est pas un MP4.
        private static readonly HashSet<string> FirstBoxes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ftyp", "wide", "free", "skip", "mdat", "moov", "pnot", "uuid", "styp",
        };

        public static bool IsMp4Family(string name) => Extensions.Contains(Path.GetExtension(name ?? ""));

        /// <summary>
        /// true : les boîtes de premier niveau couvrent exactement le fichier et moov est là.
        /// false : MP4 tronqué ou abîmé. null : illisible, ou pas un conteneur MP4 (on ne juge pas).
        /// </summary>
        public static bool? IsComplete(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
                    return IsComplete(stream);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static bool? IsComplete(Stream stream)
        {
            long length = stream.Length;
            long pos = 0;
            bool moov = false, first = true;
            var header = new byte[16];
            while (pos < length)
            {
                if (length - pos < 8) return first ? (bool?)null : false;
                stream.Position = pos;
                if (ReadFully(stream, header, 8) < 8) return false;
                long size = U32(header, 0);
                var type = Type(header, 4);
                if (first)
                {
                    if (type == null || !FirstBoxes.Contains(type)) return null;
                    first = false;
                }
                else if (type == null) return false;
                int headerSize = 8;
                if (size == 1)
                {
                    if (ReadFully(stream, header, 8) < 8) return false;
                    size = (long)U64(header, 0);
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    size = length - pos;
                }
                if (size < headerSize) return false;
                if (type == "moov") moov = true;
                pos += size;
                if (pos > length) return false;
            }
            if (first) return null;
            return moov;
        }

        /// <summary>Contenu de la boîte moov d'un fichier (sans son en-tête), null s'il n'y en a pas.</summary>
        public static byte[] ReadMoov(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
                    return ReadMoov(stream);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        public static byte[] ReadMoov(Stream stream)
        {
            long length = stream.Length, pos = 0;
            var header = new byte[16];
            while (pos + 8 <= length)
            {
                stream.Position = pos;
                if (ReadFully(stream, header, 8) < 8) return null;
                long size = U32(header, 0);
                var type = Type(header, 4);
                if (type == null) return null;
                int headerSize = 8;
                if (size == 1)
                {
                    if (ReadFully(stream, header, 8) < 8) return null;
                    size = (long)U64(header, 0);
                    headerSize = 16;
                }
                else if (size == 0) size = length - pos;
                if (size < headerSize) return null;
                if (type == "moov")
                {
                    long payload = size - headerSize;
                    if (payload > MaxMoovBytes || pos + size > length) return null;
                    var moov = new byte[payload];
                    return ReadFully(stream, moov, (int)payload) == payload ? moov : null;
                }
                pos += size;
            }
            return null;
        }

        /// <summary>
        /// Lit le début d'un flux (téléphone, non positionnable) tant que moov peut
        /// s'y trouver, dans la limite de <paramref name="max"/> octets. Renvoie le
        /// contenu de moov, ou null s'il est plus loin (vidéo non « fast start »).
        /// </summary>
        public static byte[] ReadMoovFromHead(Stream stream, int max)
        {
            var buffer = new byte[Math.Min(max, 64 * 1024)];
            int filled = 0;
            long pos = 0;
            while (true)
            {
                if (!Ensure(pos + 16)) { if (!Ensure(pos + 8)) return null; }
                long size = U32(buffer, (int)pos);
                var type = Type(buffer, (int)pos + 4);
                if (type == null) return null;
                int headerSize = 8;
                if (size == 1)
                {
                    if (!Ensure(pos + 16)) return null;
                    size = (long)U64(buffer, (int)pos + 8);
                    headerSize = 16;
                }
                if (size < headerSize) return null; // taille 0 : jusqu'à la fin, donc pas de moov après
                if (type == "moov")
                {
                    if (pos + size > max || !Ensure(pos + size)) return null;
                    var moov = new byte[size - headerSize];
                    Array.Copy(buffer, pos + headerSize, moov, 0, moov.Length);
                    return moov;
                }
                if (type == "mdat") return null;
                pos += size;
                if (pos + 8 > max) return null;
            }

            bool Ensure(long needed)
            {
                if (needed > max) return false;
                if (needed <= filled) return true;
                if (needed > buffer.Length) Array.Resize(ref buffer, (int)Math.Min(max, Math.Max(needed, buffer.Length * 2L)));
                while (filled < needed)
                {
                    int read = stream.Read(buffer, filled, buffer.Length - filled);
                    if (read <= 0) return false;
                    filled += read;
                }
                return true;
            }
        }

        /// <summary>Durée en secondes, lue dans mvhd (versions 0 et 1).</summary>
        public static bool TryDuration(byte[] moov, out double seconds)
        {
            seconds = 0;
            if (moov == null) return false;
            foreach (var box in Boxes(moov, 0, moov.Length))
            {
                if (box.type != "mvhd") continue;
                int p = box.payload;
                if (p + 4 > box.end) return false;
                int version = moov[p];
                p += 4;
                long timescale, duration;
                if (version == 1)
                {
                    if (p + 28 > box.end) return false;
                    timescale = U32(moov, p + 16);
                    duration = (long)U64(moov, p + 20);
                }
                else
                {
                    if (p + 16 > box.end) return false;
                    timescale = U32(moov, p + 8);
                    duration = U32(moov, p + 12);
                }
                if (timescale <= 0) return false;
                seconds = duration / (double)timescale;
                return true;
            }
            return false;
        }

        /// <summary>Durée d'une vidéo sur disque, null si illisible.</summary>
        public static double? Duration(string path) =>
            TryDuration(ReadMoov(path), out var seconds) ? seconds : (double?)null;

        /// <summary>Ce qu'il faut savoir d'une vidéo pour la comparer à une autre.</summary>
        internal sealed class VideoFacts
        {
            public double? Duration;
            public double? VideoDuration;
            public double? AudioDuration;

            /// <summary>Dimensions d'affichage (rotation de la matrice appliquée), 0 si inconnues.</summary>
            public int Width;
            public int Height;

            /// <summary>Rotation d'affichage de la piste vidéo (0, 90, 180, 270), lue dans la matrice de tkhd.</summary>
            public int Rotation;

            /// <summary>Clip de Live Photo iPhone (métadonnées QuickTime).</summary>
            public bool AppleLive;
        }

        private static readonly HashSet<string> Containers = new HashSet<string>(StringComparer.Ordinal)
        {
            "moov", "trak", "mdia", "minf", "stbl", "udta", "edts", "dinf",
        };

        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        public static VideoFacts Facts(byte[] moov)
        {
            if (moov == null) return null;
            var facts = new VideoFacts();
            if (TryDuration(moov, out var seconds)) facts.Duration = seconds;
            try
            {
                foreach (var trak in Boxes(moov, 0, moov.Length))
                {
                    if (trak.type != "trak") continue;
                    string handler = null;
                    double? duration = null;
                    int width = 0, height = 0, rotation = 0;
                    foreach (var box in Boxes(moov, trak.payload, trak.end))
                    {
                        if (box.type == "tkhd") TrackSize(moov, box.payload, box.end, out width, out height, out rotation);
                        if (box.type != "mdia") continue;
                        foreach (var inner in Boxes(moov, box.payload, box.end))
                        {
                            if (inner.type == "hdlr" && inner.payload + 12 <= inner.end) handler = Encoding.ASCII.GetString(moov, inner.payload + 8, 4);
                            if (inner.type == "mdhd") duration = MediaDuration(moov, inner.payload, inner.end);
                        }
                    }
                    if (handler == "vide" && facts.Width == 0)
                    {
                        facts.Width = width;
                        facts.Height = height;
                        facts.Rotation = rotation;
                        facts.VideoDuration = duration;
                    }
                    else if (handler == "soun" && facts.AudioDuration == null) facts.AudioDuration = duration;
                }
            }
            catch (IndexOutOfRangeException) { }
            catch (ArgumentException) { }
            facts.AppleLive = IsAppleLive(moov);
            return facts;
        }

        /// <summary>Largeur et hauteur de tkhd (16.16), échangées si la matrice tourne de 90° ou 270°.</summary>
        private static void TrackSize(byte[] b, int p, int end, out int width, out int height, out int rotation)
        {
            width = height = rotation = 0;
            int version = b[p];
            int matrix = p + 4 + (version == 1 ? 32 : 20) + 16;
            if (matrix + 44 > end) return;
            long a = (int)U32(b, matrix), bb = (int)U32(b, matrix + 4);
            width = (int)(U32(b, matrix + 36) >> 16);
            height = (int)(U32(b, matrix + 40) >> 16);
            long d = (int)U32(b, matrix + 16);
            if (a == 0 && bb > 0) rotation = 90;
            else if (a == 0 && bb < 0) rotation = 270;
            else if (a < 0 && d < 0) rotation = 180;
            if (a == 0 && bb != 0)
            {
                int t = width;
                width = height;
                height = t;
            }
        }

        private static double? MediaDuration(byte[] b, int p, int end)
        {
            int version = b[p];
            long timescale, duration;
            if (version == 1)
            {
                if (p + 32 > end) return null;
                timescale = U32(b, p + 20);
                duration = (long)U64(b, p + 24);
            }
            else
            {
                if (p + 20 > end) return null;
                timescale = U32(b, p + 12);
                duration = U32(b, p + 16);
            }
            return timescale > 0 ? duration / (double)timescale : (double?)null;
        }

        /// <summary>
        /// Clip de Live Photo : clé QuickTime (mdta/keys/ilst) « com.apple.quicktime.content.identifier »
        /// ou contenant « live-photo » (aussi dans la piste de métadonnées, boîtes mebx/keyd).
        /// </summary>
        public static bool IsAppleLive(byte[] moov)
        {
            if (moov == null) return false;
            foreach (var key in QuickTimeKeys(moov, 0, moov.Length, 0))
            {
                if (IsLiveKey(key)) return true;
            }
            var text = Latin1.GetString(moov);
            return text.IndexOf("com.apple.quicktime.content.identifier", StringComparison.Ordinal) >= 0
                || text.IndexOf("com.apple.quicktime.live-photo", StringComparison.Ordinal) >= 0;
        }

        private static bool IsLiveKey(string key) =>
            key == "com.apple.quicktime.content.identifier" || key.IndexOf("live-photo", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Les noms des clés « keys » (QuickTime : meta n'est pas toujours une boîte pleine).</summary>
        private static IEnumerable<string> QuickTimeKeys(byte[] b, int from, int to, int depth)
        {
            if (depth > 8) yield break;
            foreach (var box in Boxes(b, from, to))
            {
                if (Containers.Contains(box.type))
                {
                    foreach (var key in QuickTimeKeys(b, box.payload, box.end, depth + 1)) yield return key;
                }
                else if (box.type == "meta")
                {
                    // MP4 : version + drapeaux d'abord ; QuickTime : directement les boîtes.
                    int start = box.payload + 8 <= box.end && Type(b, box.payload + 4) == null && box.payload + 12 <= box.end ? box.payload + 4 : box.payload;
                    foreach (var key in QuickTimeKeys(b, start, box.end, depth + 1)) yield return key;
                }
                else if (box.type == "keys" && box.payload + 8 <= box.end)
                {
                    long count = U32(b, box.payload + 4);
                    int p = box.payload + 8;
                    for (long k = 0; k < count && p + 8 <= box.end; k++)
                    {
                        long size = U32(b, p);
                        if (size < 8 || p + size > box.end) break;
                        yield return Encoding.UTF8.GetString(b, p + 8, (int)size - 8);
                        p += (int)size;
                    }
                }
            }
        }

        private static readonly Regex Iso6709 =new Regex(@"^([+-]\d{1,2}(?:\.\d+)?)([+-]\d{1,3}(?:\.\d+)?)", RegexOptions.CultureInvariant);
        private static readonly Regex Iso6709Anywhere = new Regex(@"([+-]\d{1,2}\.\d+)([+-]\d{1,3}\.\d+)(?:[+-]\d+(?:\.\d+)?)?/", RegexOptions.CultureInvariant);

        /// <summary>Position GPS : udta/©xyz (Android), sinon la clé ISO6709 des vidéos Apple.</summary>
        public static bool TryLocation(byte[] moov, out double lat, out double lon)
        {
            lat = lon = 0;
            if (moov == null) return false;
            foreach (var udta in Boxes(moov, 0, moov.Length))
            {
                if (udta.type != "udta") continue;
                foreach (var box in Boxes(moov, udta.payload, udta.end))
                {
                    if (box.rawType != 0xA978797A) continue; // ©xyz
                    int p = box.payload;
                    if (p + 4 > box.end) continue;
                    int len = (moov[p] << 8) | moov[p + 1];
                    p += 4;
                    len = Math.Min(len, box.end - p);
                    if (len <= 0) continue;
                    var text = Encoding.ASCII.GetString(moov, p, len);
                    var m = Iso6709.Match(text);
                    if (m.Success && Parse(m, out lat, out lon)) return true;
                }
            }
            var latin = Encoding.GetEncoding(28591).GetString(moov);
            if (latin.IndexOf("ISO6709", StringComparison.Ordinal) >= 0)
            {
                var m = Iso6709Anywhere.Match(latin);
                if (m.Success && Parse(m, out lat, out lon)) return true;
            }
            return false;
        }

        private static bool Parse(Match m, out double lat, out double lon)
        {
            lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            lon = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180 && !(lat == 0 && lon == 0);
        }

        /// <summary>Les boîtes contenues dans [from, to) d'un tableau.</summary>
        internal static IEnumerable<(string type, uint rawType, int payload, int end)> Boxes(byte[] b, int from, int to)
        {
            int pos = from;
            while (pos + 8 <= to)
            {
                long size = U32(b, pos);
                uint raw = (uint)U32(b, pos + 4);
                int header = 8;
                if (size == 1)
                {
                    if (pos + 16 > to) yield break;
                    size = (long)U64(b, pos + 8);
                    header = 16;
                }
                else if (size == 0) size = to - pos;
                if (size < header || pos + size > to) yield break;
                yield return (Encoding.ASCII.GetString(b, pos + 4, 4), raw, pos + header, (int)(pos + size));
                pos += (int)size;
            }
        }

        private static string Type(byte[] b, int at)
        {
            for (int i = at; i < at + 4; i++)
            {
                if (b[i] < 0x20 || b[i] > 0x7E) return null;
            }
            return Encoding.ASCII.GetString(b, at, 4);
        }

        internal static long U32(byte[] b, int at) => ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3];

        internal static ulong U64(byte[] b, int at) => ((ulong)U32(b, at) << 32) | (ulong)U32(b, at + 4);

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0, read;
            while (total < count && (read = stream.Read(buffer, total, count - total)) > 0) total += read;
            return total;
        }
    }
}
