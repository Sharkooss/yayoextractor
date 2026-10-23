using System;
using System.Globalization;
using System.Text;

namespace YayoPont.Core
{
    /// <summary>
    /// Date de prise de vue d'un JPEG, lue dans les premiers Ko du fichier
    /// (le début du flux, déjà en mémoire pendant la copie).
    /// </summary>
    internal static class ExifDate
    {
        private const int ExifIfdPointer = 0x8769;
        private const int DateTimeOriginal = 0x9003;
        private const int DateTimeDigitized = 0x9004;
        private const int DateTimeTag = 0x0132;

        public static bool TryRead(byte[] head, out DateTime date)
        {
            date = default;
            try
            {
                return TryReadUnsafe(head ?? new byte[0], out date);
            }
            catch (IndexOutOfRangeException)
            {
                return false; // début tronqué : pas de date, la copie continue
            }
        }

        private static bool TryReadUnsafe(byte[] b, out DateTime date)
        {
            date = default;
            if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return false;
            int i = 2;
            while (i + 4 <= b.Length)
            {
                if (b[i] != 0xFF) return false;
                int marker = b[i + 1];
                if (marker == 0xDA || marker == 0xD9) return false; // données d'image : plus d'EXIF après
                int length = (b[i + 2] << 8) | b[i + 3];
                if (length < 2) return false;
                int start = i + 4;
                if (marker == 0xE1 && start + 6 <= b.Length && Encoding.ASCII.GetString(b, start, 6) == "Exif\0\0")
                {
                    return TryReadTiff(b, start + 6, Math.Min(b.Length, i + 2 + length), out date);
                }
                i += 2 + length;
            }
            return false;
        }

        private static bool TryReadTiff(byte[] b, int tiff, int end, out DateTime date)
        {
            date = default;
            bool big = b[tiff] == 0x4D;
            int U16(int at) => big ? (b[at] << 8) | b[at + 1] : b[at] | (b[at + 1] << 8);
            int U32(int at) => big
                ? (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]
                : b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

            if (U16(tiff + 2) != 42) return false;
            int ifd0 = tiff + U32(tiff + 4);
            string fallback = null;
            int exifIfd = -1;
            foreach (var (tag, valueAt) in Entries(ifd0))
            {
                if (tag == DateTimeTag) fallback = Ascii(tiff + U32(valueAt));
                else if (tag == ExifIfdPointer) exifIfd = tiff + U32(valueAt);
            }
            if (exifIfd >= 0)
            {
                foreach (var (tag, valueAt) in Entries(exifIfd))
                {
                    if ((tag == DateTimeOriginal || tag == DateTimeDigitized) && Parse(Ascii(tiff + U32(valueAt)), out date)) return true;
                }
            }
            return Parse(fallback, out date);

            System.Collections.Generic.IEnumerable<(int tag, int valueAt)> Entries(int ifd)
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
