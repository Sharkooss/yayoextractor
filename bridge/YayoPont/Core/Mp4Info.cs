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

        private static readonly Regex Iso6709 = new Regex(@"^([+-]\d{1,2}(?:\.\d+)?)([+-]\d{1,3}(?:\.\d+)?)", RegexOptions.CultureInvariant);
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
