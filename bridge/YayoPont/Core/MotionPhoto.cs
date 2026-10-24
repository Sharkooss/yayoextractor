using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace YayoPont.Core
{
    /// <summary>
    /// Photos animées de toutes les marques : une vidéo MP4 collée après l'image.
    /// JPEG : Samsung (remorque SEF), Google (XMP MicroVideo/MotionPhoto/Container),
    /// Xiaomi, Oppo/OnePlus/Realme, Vivo, Honor/Huawei (MP4 collé, avec ou sans
    /// en-tête maison). HEIC : boîte « mpvd » (Google) ou remorque SEF (Samsung).
    /// On ne garde que la photo, sans jamais produire un fichier invalide (sinon
    /// on garde l'original).
    /// </summary>
    internal static class MotionPhoto
    {
        private static readonly byte[] SamsungMarker = Encoding.ASCII.GetBytes("MotionPhoto_Data");
        private static readonly byte[] Ftyp = Encoding.ASCII.GetBytes("ftyp");
        private static readonly byte[] XmpSignature = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

        private static readonly Regex MicroVideoOffset = new Regex("MicroVideoOffset\\s*=\\s*\"(\\d+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex VideoItem = new Regex("<[^<>]*Item:Mime\\s*=\\s*\"video/[^<>]*>", RegexOptions.CultureInvariant);
        private static readonly Regex ItemLength = new Regex("Item:Length\\s*=\\s*\"(\\d+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex Flags = new Regex("(MotionPhoto|MicroVideo)(\\s*=\\s*\"|>)1(\"|<)", RegexOptions.CultureInvariant);

        /// <summary>Boîtes de premier niveau d'un HEIC/HEIF (avant une éventuelle vidéo).</summary>
        private static readonly HashSet<string> HeifBoxes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ftyp", "meta", "mdat", "free", "skip", "idat", "moov", "uuid", "wide", "pdin", "styp", "sidx", "jumb",
        };

        public static bool IsJpegName(string name)
        {
            var ext = Path.GetExtension(name ?? "");
            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsHeifName(string name)
        {
            var ext = Path.GetExtension(name ?? "");
            return ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Formats qui peuvent contenir la vidéo d'une photo animée.</summary>
        public static bool CanStrip(string name) => IsJpegName(name) || IsHeifName(name);

        public static int ImageEnd(byte[] b, int length) => ImageEnd(b, 0, length);

        /// <summary>
        /// Position juste après le FFD9 de l'image JPEG qui commence en <paramref name="from"/>,
        /// ou -1. Les segments sont sautés par leur longueur (la vignette EXIF est dans
        /// APP1), les données d'image parcourues jusqu'à un marqueur qui n'est ni un
        /// octet de bourrage (FF00) ni un RSTn ; un JPEG progressif enchaîne plusieurs scans.
        /// </summary>
        public static int ImageEnd(byte[] b, int from, int length)
        {
            if (from < 0 || length - from < 4 || b[from] != 0xFF || b[from + 1] != 0xD8) return -1;
            int i = from + 2;
            bool frame = false;
            while (i + 2 <= length)
            {
                if (b[i] != 0xFF) return -1;
                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD9) return frame ? i + 2 : -1;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                if (marker == 0xD8) return -1;
                if (i + 4 > length) return -1;
                int segment = (b[i + 2] << 8) | b[i + 3];
                if (segment < 2) return -1;
                i += 2 + segment;
                if (marker != 0xDA) continue;
                frame = true;
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

        /// <summary>Début de la vidéo collée après l'image JPEG (enregistrement Samsung compris), ou -1.</summary>
        public static int VideoStart(byte[] b, int length) => VideoStart(b, length, out _);

        private static int VideoStart(byte[] b, int length, out int imageEnd)
        {
            int end = imageEnd = ImageEnd(b, length);
            if (imageEnd < 0 || imageEnd >= length) return -1;
            int best = -1;

            // XMP Google (et Xiaomi, Oppo…) : la vidéo est à la fin, sa taille est annoncée.
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

            // Et toujours : la première boîte « ftyp » plausible après l'image (MP4 collé, en-tête
            // maison ou non ; Samsung récents : clip « MotionPhoto_AutoPlay » AVANT la vidéo principale).
            int limit = best < 0 ? length : best;
            for (int at = IndexOf(b, Ftyp, imageEnd + 4, limit); at >= 0; at = IndexOf(b, Ftyp, at + 1, limit))
            {
                if (!IsFtyp(b, at - 4, length)) continue;
                best = SamsungRecordStart(b, at - 4, imageEnd);
                break;
            }
            return best;

            void Consider(long start)
            {
                if (start >= end && start < length && IsFtyp(b, (int)start, length) && (best < 0 || start < best)) best = (int)start;
            }
        }

        /// <summary>
        /// Début de l'enregistrement SEF qui contient la vidéo en <paramref name="video"/>
        /// ([0, 0, type, 0x0A] [longueur du nom] [nom « MotionPhoto… » ou « mpv… »]), sinon <paramref name="video"/>.
        /// </summary>
        private static int SamsungRecordStart(byte[] b, int video, int from)
        {
            for (int n = 3; n <= 40; n++)
            {
                int at = video - 8 - n;
                if (at < from) break;
                if (b[at] != 0 || b[at + 1] != 0 || BitConverter.ToInt32(b, at + 4) != n) continue;
                var name = Encoding.ASCII.GetString(b, at + 8, n);
                if (name.StartsWith("MotionPhoto", StringComparison.Ordinal) || name.StartsWith("mpv", StringComparison.Ordinal)) return at;
            }
            return video;
        }

        /// <summary>
        /// Retire la vidéo : les octets restent dans <paramref name="b"/>, la nouvelle
        /// longueur est renvoyée (égale à <paramref name="length"/> si rien n'est fait).
        /// Les drapeaux XMP sont neutralisés sur place (même longueur).
        /// </summary>
        public static int Strip(byte[] b, int length)
        {
            if (b == null || length < 12) return length;
            if (b[0] == 0xFF && b[1] == 0xD8) return StripJpeg(b, length);
            if (b[4] == 'f' && b[5] == 't' && b[6] == 'y' && b[7] == 'p') return StripHeif(b, length);
            return length;
        }

        /// <summary>
        /// Coupe au début de la vidéo s'il reste une image secondaire (carte de gain
        /// HDR, MPF) entre la fin de l'image principale et la vidéo, sinon à la fin
        /// de l'image principale (en-têtes maison et remorque SEF partent avec la vidéo).
        /// </summary>
        private static int StripJpeg(byte[] b, int length)
        {
            int start = VideoStart(b, length, out int imageEnd);
            if (start < 0 || start < imageEnd) return length;
            int cut = HasImage(b, imageEnd, start) ? start : imageEnd;
            // Ceinture : le résultat commence par FFD8 et l'image principale complète finit par FFD9.
            if (ImageEnd(b, cut) != imageEnd || b[imageEnd - 2] != 0xFF || b[imageEnd - 1] != 0xD9) return length;
            NeutralizeJpeg(b, imageEnd);
            return cut;
        }

        /// <summary>Une image JPEG complète commence entre <paramref name="from"/> et <paramref name="to"/>.</summary>
        private static bool HasImage(byte[] b, int from, int to)
        {
            if (to - from < 4) return false;
            for (int at = Array.IndexOf(b, (byte)0xFF, from, to - from); at >= 0 && at + 3 < to; at = Array.IndexOf(b, (byte)0xFF, at + 1, to - at - 1))
            {
                if (b[at + 1] == 0xD8 && b[at + 2] == 0xFF && ImageEnd(b, at, to) > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// HEIC : coupe après la dernière boîte HEIF, avant la boîte « mpvd » ou la
        /// remorque SEF, seulement si toutes les données référencées par iloc restent
        /// avant la coupe.
        /// </summary>
        private static int StripHeif(byte[] b, int length)
        {
            int cut = HeifEnd(b, length, out bool mpvd);
            if (cut <= 0 || cut >= length) return length;
            if (!mpvd && !HasVideoAfter(b, cut, length)) return length;
            if (!HeifItemsBefore(b, cut, out var xmp)) return length;
            foreach (var (start, end) in xmp) NeutralizeText(b, start, end);
            return cut;
        }

        /// <summary>Fin de la dernière boîte HEIF bien formée (avant « mpvd » ou une remorque), -1 si ce n'est pas un HEIF.</summary>
        internal static int HeifEnd(byte[] b, int length, out bool mpvd)
        {
            mpvd = false;
            int pos = 0;
            bool meta = false;
            while (pos + 8 <= length)
            {
                long size = Mp4Info.U32(b, pos);
                var type = BoxType(b, pos + 4);
                if (type == "mpvd") { mpvd = true; break; }
                if (type == null || !HeifBoxes.Contains(type)) break;
                if (size == 1)
                {
                    if (pos + 16 > length) break;
                    size = (long)Mp4Info.U64(b, pos + 8);
                }
                else if (size == 0) size = length - pos;
                if (size < 8 || pos + size > length) break;
                if (pos == 0 && type != "ftyp") return -1;
                if (type == "meta") meta = true;
                pos += (int)size;
            }
            return pos > 0 && meta ? pos : -1;
        }

        /// <summary>Après les boîtes HEIF : un enregistrement « MotionPhoto_Data » ou une boîte ftyp plausible.</summary>
        private static bool HasVideoAfter(byte[] b, int from, int length)
        {
            if (IndexOf(b, SamsungMarker, from, length) >= 0) return true;
            for (int at = IndexOf(b, Ftyp, from + 4, length); at >= 0; at = IndexOf(b, Ftyp, at + 1, length))
            {
                if (IsFtyp(b, at - 4, length)) return true;
            }
            return false;
        }

        /// <summary>
        /// Toutes les données des éléments (iloc, méthode 0 : positions dans le fichier)
        /// sont avant <paramref name="cut"/> ; renvoie aussi où sont les XMP (éléments « mime » rdf+xml).
        /// </summary>
        private static bool HeifItemsBefore(byte[] b, int cut, out List<(int start, int end)> xmp)
        {
            xmp = new List<(int, int)>();
            try
            {
                foreach (var top in Mp4Info.Boxes(b, 0, cut))
                {
                    if (top.type != "meta") continue;
                    var xmpIds = new HashSet<long>();
                    (int start, int end) iloc = (-1, -1), idat = (-1, -1);
                    foreach (var box in Mp4Info.Boxes(b, top.payload + 4, top.end))
                    {
                        if (box.type == "iinf") XmpItems(b, box.payload, box.end, xmpIds);
                        else if (box.type == "iloc") iloc = (box.payload, box.end);
                        else if (box.type == "idat") idat = (box.payload, box.end);
                    }
                    if (iloc.start < 0) return false;
                    foreach (var item in IlocItems(b, iloc.start, iloc.end))
                    {
                        foreach (var (offset, len) in item.extents)
                        {
                            long start = item.method == 1 ? idat.start + offset : offset;
                            if (item.method == 1 && idat.start < 0) return false;
                            if (item.method == 0 && (len <= 0 || start < 0 || start + len > cut)) return false;
                            if (item.method == 1 && (len <= 0 || start + len > idat.end)) return false;
                            if (item.method != 2 && xmpIds.Contains(item.id)) xmp.Add(((int)start, (int)(start + len)));
                        }
                    }
                    return true;
                }
            }
            catch (IndexOutOfRangeException) { }
            catch (ArgumentException) { }
            return false;
        }

        /// <summary>Identifiants des éléments XMP d'un iinf (infe v2/v3 de type « mime », contenu rdf+xml).</summary>
        private static void XmpItems(byte[] b, int p, int end, HashSet<long> ids)
        {
            int version = b[p];
            p += 4 + (version == 0 ? 2 : 4);
            foreach (var infe in Mp4Info.Boxes(b, p, end))
            {
                if (infe.type != "infe") continue;
                int q = infe.payload;
                int v = b[q];
                q += 4;
                if (v < 2) continue;
                long id = v == 2 ? (b[q] << 8) | b[q + 1] : Mp4Info.U32(b, q);
                q += (v == 2 ? 2 : 4) + 2;
                if (q + 4 > infe.end || Encoding.ASCII.GetString(b, q, 4) != "mime") continue;
                q += 4;
                int zero = Array.IndexOf(b, (byte)0, q, infe.end - q); // nom de l'élément
                if (zero < 0) continue;
                int typeEnd = Array.IndexOf(b, (byte)0, zero + 1, infe.end - zero - 1);
                var contentType = Encoding.ASCII.GetString(b, zero + 1, (typeEnd < 0 ? infe.end : typeEnd) - zero - 1);
                if (contentType.IndexOf("rdf+xml", StringComparison.OrdinalIgnoreCase) >= 0) ids.Add(id);
            }
        }

        /// <summary>Éléments d'un iloc : identifiant, méthode de construction, étendues (position, longueur).</summary>
        internal static IEnumerable<(long id, int method, List<(long offset, long length)> extents)> IlocItems(byte[] b, int p, int end)
        {
            int version = b[p];
            p += 4;
            int offsetSize = b[p] >> 4, lengthSize = b[p] & 15, baseSize = b[p + 1] >> 4;
            int indexSize = version == 1 || version == 2 ? b[p + 1] & 15 : 0;
            p += 2;
            long count = version < 2 ? (b[p] << 8) | b[p + 1] : Mp4Info.U32(b, p);
            p += version < 2 ? 2 : 4;
            for (long k = 0; k < count; k++)
            {
                if (p >= end) throw new ArgumentException("iloc tronqué");
                long id = version < 2 ? (b[p] << 8) | b[p + 1] : Mp4Info.U32(b, p);
                p += version < 2 ? 2 : 4;
                int method = 0;
                if (version == 1 || version == 2)
                {
                    method = b[p + 1] & 15;
                    p += 2;
                }
                p += 2; // data_reference_index
                long baseOffset = Sized(b, ref p, baseSize);
                int count2 = (b[p] << 8) | b[p + 1];
                p += 2;
                var extents = new List<(long, long)>();
                for (int e = 0; e < count2; e++)
                {
                    if (indexSize > 0) Sized(b, ref p, indexSize);
                    long o = Sized(b, ref p, offsetSize);
                    long l = Sized(b, ref p, lengthSize);
                    extents.Add((baseOffset + o, l));
                }
                if (p > end) throw new ArgumentException("iloc tronqué");
                yield return (id, method, extents);
            }
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

        /// <summary>Le début du fichier annonce une photo animée (XMP), sans lire la vidéo.</summary>
        public static bool HeadSaysMotion(byte[] head)
        {
            if (head == null) return false;
            var xmp = XmpText(head, head.Length);
            return xmp != null && Flags.IsMatch(xmp);
        }

        /// <summary>
        /// Détection rapide sur la clé, sans lire toute la photo : vrai si le fichier
        /// doit être lu en entier (et raccourci s'il contient une vidéo).
        /// JPEG : il ne finit pas par FFD9 (fin d'image) et n'est pas une simple
        /// remorque Samsung sans photo animée. HEIC : ses boîtes ne couvrent pas
        /// exactement le fichier, ou il contient « mpvd ».
        /// </summary>
        public static bool LooksAnimated(string path)
        {
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                {
                    if (stream.Length < 16) return false;
                    var start = new byte[8];
                    if (Read(stream, start, 8) < 8) return false;
                    if (start[0] == 0xFF && start[1] == 0xD8) return JpegTailSaysMotion(stream);
                    if (Encoding.ASCII.GetString(start, 4, 4) == "ftyp") return HeifSaysMotion(stream);
                    return false;
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static bool JpegTailSaysMotion(Stream stream)
        {
            // Une seule lecture de la fin (4 Ko) : répertoire SEF et petits enregistrements y sont presque toujours.
            var tail = new TailReader(stream, 4096);
            var end = new byte[4];
            if (!tail.Read(stream.Length - 4, end, 4)) return true;
            if (end[2] == 0xFF && end[3] == 0xD9) return false;
            if (Encoding.ASCII.GetString(end) != "SEFT") return true;
            var names = SamsungTrailerNames(stream, tail);
            if (names == null) return true; // remorque illisible : Strip décidera
            foreach (var name in names)
            {
                if (name.StartsWith("MotionPhoto", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mpv", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool HeifSaysMotion(Stream stream)
        {
            long length = stream.Length, pos = 0;
            var header = new byte[16];
            while (pos < length)
            {
                if (length - pos < 8) return true;
                stream.Position = pos;
                if (Read(stream, header, 8) < 8) return true;
                long size = Mp4Info.U32(header, 0);
                var type = BoxType(header, 4);
                if (type == "mpvd" || type == null || !HeifBoxes.Contains(type)) return true;
                if (size == 1)
                {
                    if (Read(stream, header, 8) < 8) return true;
                    size = (long)Mp4Info.U64(header, 0);
                }
                else if (size == 0) return false;
                if (size < 8) return true;
                pos += size;
            }
            return pos != length;
        }

        /// <summary>
        /// Remorque Samsung : … [SEFH, version, nombre, entrées de 12 octets] [taille] « SEFT ».
        /// Chaque entrée pointe (en arrière depuis SEFH) un enregistrement : 2 octets,
        /// type, longueur du nom, nom. Renvoie les noms, ou null si la remorque est illisible.
        /// </summary>
        internal static List<string> SamsungTrailerNames(Stream stream, TailReader reader = null)
        {
            long length = stream.Length;
            if (length < 16) return null;
            reader = reader ?? new TailReader(stream, 4096);
            var tail = new byte[8];
            if (!reader.Read(length - 8, tail, 8) || Encoding.ASCII.GetString(tail, 4, 4) != "SEFT") return null;
            long dirSize = BitConverter.ToUInt32(tail, 0);
            long dirPos = length - 8 - dirSize;
            if (dirSize < 12 || dirSize > 64 * 1024 || dirPos < 0) return null;
            var dir = new byte[dirSize];
            if (!reader.Read(dirPos, dir, dir.Length) || Encoding.ASCII.GetString(dir, 0, 4) != "SEFH") return null;
            int count = BitConverter.ToInt32(dir, 8);
            if (count < 0 || 12 + count * 12L > dir.Length) return null;
            var names = new List<string>();
            var record = new byte[8 + 64];
            for (int k = 0; k < count; k++)
            {
                long offset = BitConverter.ToUInt32(dir, 12 + k * 12 + 4);
                long at = dirPos - offset;
                if (at < 0 || at + 8 > dirPos) return null;
                int got = (int)Math.Min(record.Length, dirPos - at);
                if (!reader.Read(at, record, got)) return null;
                int nameLength = BitConverter.ToInt32(record, 4);
                if (nameLength <= 0 || nameLength > 64 || 8 + nameLength > got) return null;
                names.Add(Encoding.ASCII.GetString(record, 8, nameLength));
            }
            return names;
        }

        /// <summary>Lectures à des positions données, servies par la fin du fichier déjà lue quand c'est possible.</summary>
        internal sealed class TailReader
        {
            private readonly Stream stream;
            private readonly byte[] block;
            private readonly long blockStart;

            public TailReader(Stream stream, int size)
            {
                this.stream = stream;
                blockStart = Math.Max(0, stream.Length - size);
                block = new byte[stream.Length - blockStart];
                stream.Position = blockStart;
                if (MotionPhoto.Read(stream, block, block.Length) < block.Length) block = new byte[0];
            }

            public bool Read(long position, byte[] buffer, int count)
            {
                if (position < 0 || count < 0 || position + count > stream.Length) return false;
                if (block.Length > 0 && position >= blockStart && position + count <= blockStart + block.Length)
                {
                    Array.Copy(block, position - blockStart, buffer, 0, count);
                    return true;
                }
                stream.Position = position;
                return MotionPhoto.Read(stream, buffer, count) == count;
            }
        }

        /// <summary>MotionPhoto="1" → "0", MicroVideo="1" → "0", tailles de la vidéo mises à zéro (même longueur).</summary>
        private static void NeutralizeJpeg(byte[] b, int imageEnd)
        {
            foreach (var (start, end) in XmpSegments(b, imageEnd)) NeutralizeText(b, start, end);
        }

        private static void NeutralizeText(byte[] b, int start, int end)
        {
            if (start < 0 || end > b.Length || end <= start) return;
            var text = Latin1.GetString(b, start, end - start);
            foreach (Match m in Flags.Matches(text)) b[start + m.Groups[3].Index - 1] = (byte)'0';
            foreach (Match m in MicroVideoOffset.Matches(text)) Zero(b, start + m.Groups[1].Index, m.Groups[1].Length);
            foreach (Match item in VideoItem.Matches(text))
            {
                var len = ItemLength.Match(item.Value);
                if (len.Success) Zero(b, start + item.Index + len.Groups[1].Index, len.Groups[1].Length);
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
                text.Append(Latin1.GetString(b, start, end - start));
            }
            return text?.ToString();
        }

        /// <summary>Segments APP1 XMP de l'en-tête JPEG (avant le premier scan).</summary>
        private static IEnumerable<(int start, int end)> XmpSegments(byte[] b, int limit)
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

        private static string BoxType(byte[] b, int at)
        {
            for (int i = at; i < at + 4; i++)
            {
                if (b[i] < 0x20 || b[i] > 0x7E) return null;
            }
            return Encoding.ASCII.GetString(b, at, 4);
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
