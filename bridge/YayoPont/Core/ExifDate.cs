using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace YayoPont.Core
{
    /// <summary>Date de prise de vue d'une photo, lue dans le début du fichier (voir <see cref="ExifInfo"/>).</summary>
    internal static class ExifDate
    {
        public static bool TryRead(byte[] head, out DateTime date)
        {
            var info = ExifInfo.Read(head);
            date = info.Date ?? default;
            return info.Date.HasValue;
        }
    }

    /// <summary>
    /// EXIF utile au rangement : date de prise de vue et position GPS. Lu dans les
    /// premiers Ko d'un JPEG (segment APP1) ou d'un HEIC (élément « Exif » du
    /// conteneur ISO-BMFF, si ses données sont dans la tête lue).
    /// </summary>
    internal sealed class ExifInfo
    {
        private const int ExifIfdPointer = 0x8769;
        private const int GpsIfdPointer = 0x8825;
        private const int DateTimeOriginal = 0x9003;
        private const int DateTimeDigitized = 0x9004;
        private const int DateTimeTag = 0x0132;

        public DateTime? Date;
        public double? Lat;
        public double? Lon;

        public static ExifInfo Read(byte[] head)
        {
            var info = new ExifInfo();
            if (head == null || head.Length < 12) return info;
            try
            {
                if (head[0] == 0xFF && head[1] == 0xD8) info.FromJpeg(head);
                else if (Encoding.ASCII.GetString(head, 4, 4) == "ftyp") info.FromHeif(head);
            }
            catch (IndexOutOfRangeException) { } // début tronqué : on garde ce qui a été lu
            catch (ArgumentException) { }
            catch (OverflowException) { }
            return info;
        }

        /// <summary>
        /// Lit la tête d'une photo JPEG jusqu'au début de l'image (où s'arrêtent EXIF
        /// et XMP), dans la limite de <paramref name="max"/> octets ; les autres
        /// formats : <paramref name="max"/> octets.
        /// </summary>
        public static byte[] ReadHead(Stream stream, int max)
        {
            var buffer = new byte[Math.Min(max, 64 * 1024)];
            int filled = 0;
            while (true)
            {
                int read;
                while (filled < buffer.Length && (read = stream.Read(buffer, filled, buffer.Length - filled)) > 0) filled += read;
                if (filled < buffer.Length || buffer.Length >= max || HeaderComplete(buffer, filled)) break;
                Array.Resize(ref buffer, Math.Min(max, buffer.Length * 2));
            }
            if (filled == buffer.Length) return buffer;
            var head = new byte[filled];
            Array.Copy(buffer, head, filled);
            return head;
        }

        /// <summary>Le début de l'image (SOS) est atteint, ou ce n'est pas un JPEG : inutile de lire plus.</summary>
        private static bool HeaderComplete(byte[] b, int length)
        {
            if (length < 4 || b[0] != 0xFF || b[1] != 0xD8) return true;
            int i = 2;
            while (i + 4 <= length)
            {
                if (b[i] != 0xFF) return true;
                if (b[i + 1] == 0xDA || b[i + 1] == 0xD9) return true;
                i += 2 + ((b[i + 2] << 8) | b[i + 3]);
            }
            return false;
        }

        private void FromJpeg(byte[] b)
        {
            int i = 2;
            while (i + 4 <= b.Length)
            {
                if (b[i] != 0xFF) return;
                int marker = b[i + 1];
                if (marker == 0xDA || marker == 0xD9) return; // données d'image : plus d'EXIF après
                int length = (b[i + 2] << 8) | b[i + 3];
                if (length < 2) return;
                int start = i + 4;
                if (marker == 0xE1 && start + 6 <= b.Length && Encoding.ASCII.GetString(b, start, 6) == "Exif\0\0")
                {
                    FromTiff(b, start + 6, Math.Min(b.Length, i + 2 + length));
                    return;
                }
                i += 2 + length;
            }
        }

        /// <summary>HEIC/HEIF : meta → iinf (élément de type « Exif ») → iloc (où sont ses octets).</summary>
        private void FromHeif(byte[] b)
        {
            foreach (var top in Mp4Info.Boxes(b, 0, b.Length))
            {
                if (top.type != "meta") continue;
                int metaStart = top.payload + 4; // boîte « pleine » : version + drapeaux
                long exifId = -1;
                (int start, int end) iloc = (-1, -1), idat = (-1, -1);
                foreach (var box in Mp4Info.Boxes(b, metaStart, top.end))
                {
                    if (box.type == "iinf") exifId = ExifItem(b, box.payload, box.end);
                    else if (box.type == "iloc") iloc = (box.payload, box.end);
                    else if (box.type == "idat") idat = (box.payload, box.end);
                }
                if (exifId < 0 || iloc.start < 0) return;
                if (!Locate(b, iloc.start, iloc.end, exifId, out int method, out long offset, out long length)) return;
                if (method == 1)
                {
                    if (idat.start < 0) return;
                    offset += idat.start;
                }
                else if (method != 0) return;
                if (offset < 0 || offset + 4 > b.Length) return;
                long tiff = offset + 4 + Mp4Info.U32(b, (int)offset);
                long end = Math.Min(b.Length, offset + length);
                if (tiff + 8 > end) return;
                FromTiff(b, (int)tiff, (int)end);
                return;
            }
        }

        private static long ExifItem(byte[] b, int p, int end)
        {
            int version = b[p];
            p += 4;
            p += version == 0 ? 2 : 4; // nombre d'entrées
            foreach (var infe in Mp4Info.Boxes(b, p, end))
            {
                if (infe.type != "infe") continue;
                int q = infe.payload;
                int v = b[q];
                q += 4;
                if (v < 2) continue;
                long id = v == 2 ? (b[q] << 8) | b[q + 1] : Mp4Info.U32(b, q);
                q += v == 2 ? 2 : 4;
                q += 2; // protection
                if (q + 4 <= infe.end && Encoding.ASCII.GetString(b, q, 4) == "Exif") return id;
            }
            return -1;
        }

        private static bool Locate(byte[] b, int p, int end, long wanted, out int method, out long offset, out long length)
        {
            method = 0;
            offset = length = 0;
            int version = b[p];
            p += 4;
            int offsetSize = b[p] >> 4, lengthSize = b[p] & 15, baseSize = b[p + 1] >> 4;
            int indexSize = version == 1 || version == 2 ? b[p + 1] & 15 : 0;
            p += 2;
            long count = version < 2 ? (b[p] << 8) | b[p + 1] : Mp4Info.U32(b, p);
            p += version < 2 ? 2 : 4;
            for (long k = 0; k < count && p < end; k++)
            {
                long id = version < 2 ? (b[p] << 8) | b[p + 1] : Mp4Info.U32(b, p);
                p += version < 2 ? 2 : 4;
                int itemMethod = 0;
                if (version == 1 || version == 2)
                {
                    itemMethod = b[p + 1] & 15;
                    p += 2;
                }
                p += 2; // data_reference_index
                long baseOffset = Sized(b, ref p, baseSize);
                int extents = (b[p] << 8) | b[p + 1];
                p += 2;
                long firstOffset = 0, firstLength = 0;
                for (int e = 0; e < extents; e++)
                {
                    if (indexSize > 0) Sized(b, ref p, indexSize);
                    long o = Sized(b, ref p, offsetSize);
                    long l = Sized(b, ref p, lengthSize);
                    if (e == 0) { firstOffset = o; firstLength = l; }
                }
                if (id != wanted) continue;
                method = itemMethod;
                offset = baseOffset + firstOffset;
                length = firstLength;
                return extents > 0;
            }
            return false;
        }

        private static long Sized(byte[] b, ref int p, int size)
        {
            long value;
            switch (size)
            {
                case 0: return 0;
                case 4: value = Mp4Info.U32(b, p); break;
                case 8: value = (long)Mp4Info.U64(b, p); break;
                default: throw new ArgumentException("taille iloc inattendue");
            }
            p += size;
            return value;
        }

        private void FromTiff(byte[] b, int tiff, int end)
        {
            bool big = b[tiff] == 0x4D;
            int U16(int at) => big ? (b[at] << 8) | b[at + 1] : b[at] | (b[at + 1] << 8);
            long U32(int at) => big
                ? ((long)b[at] << 24) | ((long)b[at + 1] << 16) | ((long)b[at + 2] << 8) | b[at + 3]
                : b[at] | ((long)b[at + 1] << 8) | ((long)b[at + 2] << 16) | ((long)b[at + 3] << 24);

            if (U16(tiff + 2) != 42) return;
            int ifd0 = Offset(U32(tiff + 4));
            string fallback = null;
            int exifIfd = -1, gpsIfd = -1;
            foreach (var (tag, valueAt) in Entries(ifd0))
            {
                if (tag == DateTimeTag) fallback = Ascii(Offset(U32(valueAt)));
                else if (tag == ExifIfdPointer) exifIfd = Offset(U32(valueAt));
                else if (tag == GpsIfdPointer) gpsIfd = Offset(U32(valueAt));
            }
            foreach (var (tag, valueAt) in Entries(exifIfd))
            {
                if ((tag == DateTimeOriginal || tag == DateTimeDigitized) && Parse(Ascii(Offset(U32(valueAt))), out var date))
                {
                    Date = date;
                    break;
                }
            }
            if (Date == null && Parse(fallback, out var dateTime)) Date = dateTime;

            char latRef = '\0', lonRef = '\0';
            double? lat = null, lon = null;
            foreach (var (tag, valueAt) in Entries(gpsIfd))
            {
                switch (tag)
                {
                    case 1: latRef = (char)b[valueAt]; break;
                    case 2: lat = Degrees(Offset(U32(valueAt))); break;
                    case 3: lonRef = (char)b[valueAt]; break;
                    case 4: lon = Degrees(Offset(U32(valueAt))); break;
                }
            }
            if (lat.HasValue && lon.HasValue && (latRef == 'N' || latRef == 'S') && (lonRef == 'E' || lonRef == 'W'))
            {
                double la = latRef == 'S' ? -lat.Value : lat.Value;
                double lo = lonRef == 'W' ? -lon.Value : lon.Value;
                if (Math.Abs(la) <= 90 && Math.Abs(lo) <= 180 && !(la == 0 && lo == 0))
                {
                    Lat = la;
                    Lon = lo;
                }
            }

            int Offset(long relative) => relative <= 0 || relative > end ? -1 : (int)(tiff + relative);

            IEnumerable<(int tag, int valueAt)> Entries(int ifd)
            {
                if (ifd < 0 || ifd + 2 > end) yield break;
                int count = U16(ifd);
                for (int k = 0; k < count && ifd + 2 + k * 12 + 12 <= end; k++)
                {
                    int entry = ifd + 2 + k * 12;
                    yield return (U16(entry), entry + 8);
                }
            }

            string Ascii(int at)
            {
                if (at < 0 || at + 19 > end) return null;
                return Encoding.ASCII.GetString(b, at, 19);
            }

            // Trois rationnels : degrés, minutes, secondes.
            double? Degrees(int at)
            {
                if (at < 0 || at + 24 > end) return null;
                double total = 0;
                for (int k = 0; k < 3; k++)
                {
                    long num = U32(at + k * 8), den = U32(at + k * 8 + 4);
                    if (den == 0) { if (num == 0) continue; return null; }
                    total += num / (double)den / Math.Pow(60, k);
                }
                return total;
            }
        }

        private static bool Parse(string text, out DateTime date)
        {
            date = default;
            return text != null
                && !text.StartsWith("0000")
                && DateTime.TryParseExact(text, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
    }
}
