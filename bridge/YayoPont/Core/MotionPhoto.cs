using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace YayoPont.Core
{
    /// <summary>
    /// Photos animées (Motion Photo Samsung, Google MicroVideo/MotionPhoto) : une
    /// vidéo MP4 collée après l'image JPEG. On la retire pour ne garder que la
    /// photo, sans jamais rendre un JPEG invalide (sinon on garde l'original).
    /// </summary>
    internal static class MotionPhoto
    {
        private static readonly byte[] SamsungMarker = Encoding.ASCII.GetBytes("MotionPhoto_Data");
        private static readonly byte[] Ftyp = Encoding.ASCII.GetBytes("ftyp");
        private static readonly byte[] XmpSignature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

        private static readonly Regex MicroVideoOffset = new Regex("MicroVideoOffset\\s*=\\s*\"(\\d+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex VideoItem = new Regex("<[^<>]*Item:Mime\\s*=\\s*\"video/[^<>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex ItemLength = new Regex("Item:Length\\s*=\\s*\"(\\d+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex Flags = new Regex("(MotionPhoto|MicroVideo)(\\s*=\\s*\"|>)1(\"|<)", RegexOptions.CultureInvariant);

        public static bool IsJpegName(string name)
        {
            var ext = Path.GetExtension(name ?? "");
            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Position juste après le FFD9 de l'image principale, ou -1. Les segments
        /// sont sautés par leur longueur (la vignette EXIF est dans APP1), les
        /// données d'image parcourues jusqu'à un marqueur qui n'est ni un octet de
        /// bourrage (FF00) ni un RSTn ; un JPEG progressif enchaîne plusieurs scans.
        /// </summary>
        public static int ImageEnd(byte[] b, int length)
        {
            if (length < 4 || b[0] != 0xFF || b[1] != 0xD8) return -1;
            int i = 2;
            while (i + 2 <= length)
            {
                if (b[i] != 0xFF) return -1;
                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD9) return i + 2;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                if (i + 4 > length) return -1;
                int segment = (b[i + 2] << 8) | b[i + 3];
                if (segment < 2) return -1;
                i += 2 + segment;
                if (marker != 0xDA) continue;
                while (true)
                {
                    int ff = i < length ? Array.IndexOf(b, (byte)0xFF, i, length - i) : -1;
                    if (ff < 0 || ff + 1 >= length) return -1;
                    int next = b[ff + 1];
                    if (next == 0x00 || (next >= 0xD0 && next <= 0xD7)) { i = ff + 2; continue; }
                    if (next == 0xFF) { i = ff + 1; continue; }
                    i = ff;
                    break;
                }
            }
            return -1;
        }

        /// <summary>Début de la vidéo collée après l'image, ou -1 s'il n'y en a pas.</summary>
        public static int VideoStart(byte[] b, int length) => VideoStart(b, length, out _);

        private static int VideoStart(byte[] b, int length, out int imageEnd)
        {
            int end = imageEnd = ImageEnd(b, length);
            if (imageEnd < 0 || imageEnd >= length) return -1;
            int best = -1;

            // XMP Google : la vidéo est à la fin, sa taille est annoncée.
            var xmp = XmpText(b, imageEnd);
            if (xmp != null)
            {
                var offset = MicroVideoOffset.Match(xmp);
                if (offset.Success) Consider(length - Number(offset.Groups[1].Value));
                foreach (Match item in VideoItem.Matches(xmp))
                {
                    var len = ItemLength.Match(item.Value);
                    if (len.Success) Consider(length - Number(len.Groups[1].Value));
                }
            }

            // Samsung : « MotionPhoto_Data » juste avant le MP4, dans un enregistrement SEF.
            int samsung = IndexOf(b, SamsungMarker, imageEnd, length);
            if (samsung >= 0)
            {
                int start = samsung;
                if (samsung - 8 >= imageEnd && BitConverter.ToInt32(b, samsung - 4) == SamsungMarker.Length) start = samsung - 8;
                if (best < 0 || start < best) best = start;
            }

            // Sinon : une boîte « ftyp » plausible après l'image.
            if (best < 0)
            {
                for (int at = IndexOf(b, Ftyp, imageEnd + 4, length); at >= 0; at = IndexOf(b, Ftyp, at + 1, length))
                {
                    if (IsFtyp(b, at - 4, length)) { best = at - 4; break; }
                }
            }
            return best;

            void Consider(long start)
            {
                if (start >= end && start < length && IsFtyp(b, (int)start, length) && (best < 0 || start < best)) best = (int)start;
            }
        }

        /// <summary>
        /// Retire la vidéo : les octets restent dans <paramref name="b"/>, la nouvelle
        /// longueur est renvoyée (égale à <paramref name="length"/> si rien n'est fait).
        /// Les drapeaux XMP sont neutralisés sur place (même longueur).
        /// </summary>
        public static int Strip(byte[] b, int length)
        {
            int start = VideoStart(b, length, out int imageEnd);
            if (start < 0 || start < imageEnd) return length;
            Neutralize(b, imageEnd);
            // Ceinture : l'image principale doit rester intacte.
            return ImageEnd(b, start) == imageEnd ? start : length;
        }

        /// <summary>Le début du fichier annonce une photo animée (XMP), sans lire la vidéo.</summary>
        public static bool HeadSaysMotion(byte[] head)
        {
            if (head == null) return false;
            var xmp = XmpText(head, head.Length);
            return xmp != null && Flags.IsMatch(xmp);
        }

        /// <summary>
        /// Photo animée sur disque, détectée sans tout lire : drapeau XMP en tête,
        /// ou enregistrement « MotionPhoto_Data » dans la remorque Samsung (SEF).
        /// </summary>
        public static bool LooksAnimated(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                {
                    var head = new byte[(int)Math.Min(stream.Length, 192 * 1024)];
                    int filled = 0, read;
                    while (filled < head.Length && (read = stream.Read(head, filled, head.Length - filled)) > 0) filled += read;
                    if (HeadSaysMotion(head)) return true;
                    return SamsungTrailerHasMotion(stream);
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// Remorque Samsung : … [SEFH, version, nombre, entrées de 12 octets] [taille] « SEFT ».
        /// Chaque entrée pointe (en arrière depuis SEFH) un enregistrement : 2 octets,
        /// type, longueur du nom, nom. On cherche le nom « MotionPhoto_Data ».
        /// </summary>
        internal static bool SamsungTrailerHasMotion(Stream stream)
        {
            long length = stream.Length;
            if (length < 16) return false;
            var tail = new byte[8];
            stream.Position = length - 8;
            if (Read(stream, tail, 8) < 8 || Encoding.ASCII.GetString(tail, 4, 4) != "SEFT") return false;
            long dirSize = BitConverter.ToUInt32(tail, 0);
            long dirPos = length - 8 - dirSize;
            if (dirSize < 12 || dirSize > 64 * 1024 || dirPos < 0) return false;
            var dir = new byte[dirSize];
            stream.Position = dirPos;
            if (Read(stream, dir, dir.Length) < dir.Length || Encoding.ASCII.GetString(dir, 0, 4) != "SEFH") return false;
            int count = BitConverter.ToInt32(dir, 8);
            var record = new byte[8 + SamsungMarker.Length];
            for (int k = 0; k < count && 12 + k * 12 + 12 <= dir.Length; k++)
            {
                long offset = BitConverter.ToUInt32(dir, 12 + k * 12 + 4);
                long at = dirPos - offset;
                if (at < 0 || at + record.Length > length) continue;
                stream.Position = at;
                if (Read(stream, record, record.Length) < record.Length) continue;
                if (BitConverter.ToInt32(record, 4) == SamsungMarker.Length && IndexOf(record, SamsungMarker, 8, record.Length) == 8) return true;
            }
            return false;
        }

        /// <summary>MotionPhoto="1" → "0", MicroVideo="1" → "0", tailles de la vidéo mises à zéro (même longueur).</summary>
        private static void Neutralize(byte[] b, int imageEnd)
        {
            foreach (var (start, end) in XmpSegments(b, imageEnd))
            {
                var text = Encoding.GetEncoding(28591).GetString(b, start, end - start);
                foreach (Match m in Flags.Matches(text)) b[start + m.Groups[3].Index - 1] = (byte)'0';
                foreach (Match m in MicroVideoOffset.Matches(text)) Zero(b, start + m.Groups[1].Index, m.Groups[1].Length);
                foreach (Match item in VideoItem.Matches(text))
                {
                    var len = ItemLength.Match(item.Value);
                    if (len.Success) Zero(b, start + item.Index + len.Groups[1].Index, len.Groups[1].Length);
                }
            }
        }

        private static void Zero(byte[] b, int at, int count)
        {
            for (int i = 0; i < count; i++) b[at + i] = (byte)'0';
        }

        private static string XmpText(byte[] b, int limit)
        {
            StringBuilder text = null;
            foreach (var (start, end) in XmpSegments(b, limit))
            {
                text = text ?? new StringBuilder();
                text.Append(Encoding.GetEncoding(28591).GetString(b, start, end - start));
            }
            return text?.ToString();
        }

        /// <summary>Segments APP1 XMP de l'en-tête JPEG (avant le premier scan).</summary>
        private static System.Collections.Generic.IEnumerable<(int start, int end)> XmpSegments(byte[] b, int limit)
        {
            if (limit < 4 || b[0] != 0xFF || b[1] != 0xD8) yield break;
            int i = 2;
            while (i + 4 <= limit && b[i] == 0xFF)
            {
                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xDA || marker == 0xD9) yield break;
                int segment = (b[i + 2] << 8) | b[i + 3];
                if (segment < 2) yield break;
                int start = i + 4, end = Math.Min(limit, i + 2 + segment);
                if (marker == 0xE1 && IndexOf(b, XmpSignature, start, Math.Min(end, start + XmpSignature.Length)) == start)
                    yield return (start, end);
                i += 2 + segment;
            }
        }

        private static bool IsFtyp(byte[] b, int at, int length)
        {
            if (at < 0 || at + 12 > length) return false;
            long size = Mp4Info.U32(b, at);
            if (size < 8 || size > 4096 || b[at + 4] != 'f' || b[at + 5] != 't' || b[at + 6] != 'y' || b[at + 7] != 'p') return false;
            for (int i = at + 8; i < at + 12; i++)
            {
                if (b[i] < 0x20 || b[i] > 0x7E) return false;
            }
            return true;
        }

        private static long Number(string digits) => long.TryParse(digits, out var n) ? n : -1;

        internal static int IndexOf(byte[] b, byte[] pattern, int from, int to)
        {
            if (from < 0) from = 0;
            for (int i = from; i + pattern.Length <= to; i++)
            {
                i = Array.IndexOf(b, pattern[0], i, to - i);
                if (i < 0 || i + pattern.Length > to) return -1;
                int k = 1;
                while (k < pattern.Length && b[i + k] == pattern[k]) k++;
                if (k == pattern.Length) return i;
            }
            return -1;
        }

        private static int Read(Stream stream, byte[] buffer, int count)
        {
            int total = 0, read;
            while (total < count && (read = stream.Read(buffer, total, count - total)) > 0) total += read;
            return total;
        }
    }
}
