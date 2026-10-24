using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace YayoPont.Tests
{
    /// <summary>Fabrique de fichiers synthétiques : MP4, photos animées, EXIF avec GPS, HEIC.</summary>
    internal static class MediaFactory
    {
        public static byte[] BE32(long v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

        public static byte[] LE32(long v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

        public static byte[] Box(string type, params byte[][] payload)
        {
            var body = payload.SelectMany(p => p).ToArray();
            return BE32(8 + body.Length).Concat(Encoding.ASCII.GetBytes(type)).Concat(body).ToArray();
        }

        private static byte[] RawBox(byte[] type, params byte[][] payload)
        {
            var body = payload.SelectMany(p => p).ToArray();
            return BE32(8 + body.Length).Concat(type).Concat(body).ToArray();
        }

        public static byte[] Mvhd(double seconds, int version = 0)
        {
            const int timescale = 1000;
            long duration = (long)(seconds * timescale);
            var p = new List<byte> { (byte)version, 0, 0, 0 };
            if (version == 1)
            {
                p.AddRange(new byte[16]);
                p.AddRange(BE32(timescale));
                p.AddRange(BE32(duration >> 32));
                p.AddRange(BE32(duration & 0xFFFFFFFF));
            }
            else
            {
                p.AddRange(new byte[8]);
                p.AddRange(BE32(timescale));
                p.AddRange(BE32(duration));
            }
            p.AddRange(new byte[80]);
            return Box("mvhd", p.ToArray());
        }

        public static byte[] Xyz(string iso6709)
        {
            var text = Encoding.ASCII.GetBytes(iso6709);
            var payload = new byte[] { (byte)(text.Length >> 8), (byte)text.Length, 0x15, 0xC7 }.Concat(text).ToArray();
            return RawBox(new byte[] { 0xA9, (byte)'x', (byte)'y', (byte)'z' }, payload);
        }

        /// <summary>MP4 minimal : ftyp, moov (mvhd, udta/©xyz), mdat ; moov avant ou après mdat.</summary>
        public static byte[] Mp4(double seconds = 3, string location = null, int mdatBytes = 1000, bool moovFirst = true, int mvhdVersion = 0)
        {
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("isom"), BE32(512), Encoding.ASCII.GetBytes("isomiso2mp41"));
            var moovParts = new List<byte[]> { Mvhd(seconds, mvhdVersion) };
            if (location != null) moovParts.Add(Box("udta", Xyz(location)));
            var moov = Box("moov", moovParts.ToArray());
            var mdat = Box("mdat", Enumerable.Range(0, mdatBytes).Select(i => (byte)(i * 7)).ToArray());
            return moovFirst ? ftyp.Concat(moov).Concat(mdat).ToArray() : ftyp.Concat(mdat).Concat(moov).ToArray();
        }

        public static byte[] Jpeg(int w = 64, int h = 48) => PhoneSourceTests.Jpeg(w, h);

        /// <summary>Insère un segment APP1 juste après SOI.</summary>
        public static byte[] WithApp1(byte[] jpeg, byte[] app1Payload)
        {
            var segment = new byte[] { 0xFF, 0xE1, (byte)((app1Payload.Length + 2) >> 8), (byte)(app1Payload.Length + 2) };
            return jpeg.Take(2).Concat(segment).Concat(app1Payload).Concat(jpeg.Skip(2)).ToArray();
        }

        public static byte[] WithXmp(byte[] jpeg, string xmp) =>
            WithApp1(jpeg, Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0").Concat(Encoding.UTF8.GetBytes(xmp)).ToArray());

        /// <summary>Photo animée Google : XMP MotionPhoto + Container (taille de la vidéo), puis la vidéo à la fin.</summary>
        public static byte[] GoogleMotion(byte[] video, byte[] between = null, bool microVideo = false)
        {
            var xmp = microVideo
                ? $"<x:xmpmeta><rdf:Description GCamera:MicroVideo=\"1\" GCamera:MicroVideoVersion=\"1\" GCamera:MicroVideoOffset=\"{video.Length}\"/></x:xmpmeta>"
                : "<x:xmpmeta><rdf:Description GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\"><Container:Directory><rdf:Seq>"
                  + "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"image/jpeg\" Item:Semantic=\"Primary\" Item:Length=\"0\" Item:Padding=\"0\"/></rdf:li>"
                  + $"<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{video.Length}\" Item:Padding=\"0\"/></rdf:li>"
                  + "</rdf:Seq></Container:Directory></rdf:Description></x:xmpmeta>";
            var photo = WithXmp(Jpeg(), xmp);
            return photo.Concat(between ?? new byte[0]).Concat(video).ToArray();
        }

        /// <summary>Photo animée Samsung : JPEG, enregistrement SEF « MotionPhoto_Data » + MP4, remorque SEFH…SEFT.</summary>
        public static byte[] SamsungMotion(byte[] jpeg, byte[] video)
        {
            var name = Encoding.ASCII.GetBytes("MotionPhoto_Data");
            var record = new byte[] { 0, 0, 0x30, 0x0A }.Concat(LE32(name.Length)).Concat(name).Concat(video).ToArray();
            int recordStart = jpeg.Length;
            int sefh = recordStart + record.Length;
            var dir = Encoding.ASCII.GetBytes("SEFH").Concat(LE32(107)).Concat(LE32(1))
                .Concat(new byte[] { 0, 0, 0x30, 0x0A }).Concat(LE32(sefh - recordStart)).Concat(LE32(record.Length)).ToArray();
            return jpeg.Concat(record).Concat(dir).Concat(LE32(dir.Length)).Concat(Encoding.ASCII.GetBytes("SEFT")).ToArray();
        }

        /// <summary>Remorque Samsung sans vidéo (comme une photo ordinaire d'un Galaxy).</summary>
        public static byte[] SamsungPlain(byte[] jpeg)
        {
            var name = Encoding.ASCII.GetBytes("Image_UTC_Data");
            var record = new byte[] { 0, 0, 0x01, 0x0A }.Concat(LE32(name.Length)).Concat(name).Concat(Encoding.ASCII.GetBytes("1700000000000")).ToArray();
            int sefh = jpeg.Length + record.Length;
            var dir = Encoding.ASCII.GetBytes("SEFH").Concat(LE32(107)).Concat(LE32(1))
                .Concat(new byte[] { 0, 0, 0x01, 0x0A }).Concat(LE32(sefh - jpeg.Length)).Concat(LE32(record.Length)).ToArray();
            return jpeg.Concat(record).Concat(dir).Concat(LE32(dir.Length)).Concat(Encoding.ASCII.GetBytes("SEFT")).ToArray();
        }

        /// <summary>TIFF EXIF petit-boutiste : date de prise de vue, et GPS si donné.</summary>
        public static byte[] ExifTiff(string date, double? lat = null, double? lon = null)
        {
            bool gps = lat.HasValue && lon.HasValue;
            var t = new List<byte>();
            void U16(int v) { t.Add((byte)v); t.Add((byte)(v >> 8)); }
            void U32(long v) { t.AddRange(LE32(v)); }
            void Entry(int tag, int type, int count, long value) { U16(tag); U16(type); U32(count); U32(value); }

            int ifd0 = 8, ifd0Size = 2 + (gps ? 2 : 1) * 12 + 4;
            int exif = ifd0 + ifd0Size, exifSize = 2 + 12 + 4;
            int gpsIfd = exif + exifSize, gpsSize = gps ? 2 + 4 * 12 + 4 : 0;
            int data = gpsIfd + gpsSize;
            t.AddRange(new byte[] { 0x49, 0x49 });
            U16(42);
            U32(8);
            U16(gps ? 2 : 1);
            Entry(0x8769, 4, 1, exif);
            if (gps) Entry(0x8825, 4, 1, gpsIfd);
            U32(0);
            U16(1);
            Entry(0x9003, 2, 20, data);
            U32(0);
            if (gps)
            {
                U16(4);
                U16(1); U16(2); U32(2); t.AddRange(new byte[] { (byte)(lat >= 0 ? 'N' : 'S'), 0, 0, 0 });
                Entry(2, 5, 3, data + 20);
                U16(3); U16(2); U32(2); t.AddRange(new byte[] { (byte)(lon >= 0 ? 'E' : 'W'), 0, 0, 0 });
                Entry(4, 5, 3, data + 44);
                U32(0);
            }
            t.AddRange(Encoding.ASCII.GetBytes(date + "\0"));
            if (gps)
            {
                foreach (var value in new[] { Math.Abs(lat.Value), Math.Abs(lon.Value) })
                {
                    int deg = (int)value;
                    double minutes = (value - deg) * 60;
                    int min = (int)minutes;
                    long sec = (long)Math.Round((minutes - min) * 60 * 10000);
                    U32(deg); U32(1); U32(min); U32(1); U32(sec); U32(10000);
                }
            }
            return t.ToArray();
        }

        public static byte[] JpegWithExif(string date, double? lat = null, double? lon = null) =>
            WithApp1(Jpeg(), Encoding.ASCII.GetBytes("Exif\0\0").Concat(ExifTiff(date, lat, lon)).ToArray());

        /// <summary>HEIC minimal : ftyp, meta (hdlr, iinf avec un élément « Exif », iloc), mdat contenant l'EXIF.</summary>
        public static byte[] Heic(string date, double? lat = null, double? lon = null, int imageBytes = 2000)
        {
            var exifPayload = BE32(6).Concat(Encoding.ASCII.GetBytes("Exif\0\0")).Concat(ExifTiff(date, lat, lon)).ToArray();
            byte[] Full(string type, int version, params byte[][] payload) => Box(type, new[] { new byte[] { (byte)version, 0, 0, 0 } }.Concat(payload).ToArray());
            byte[] U16(int v) => new[] { (byte)(v >> 8), (byte)v };
            byte[] Infe(int id, string type) => Full("infe", 2, U16(id), U16(0), Encoding.ASCII.GetBytes(type), new byte[] { 0 });

            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("heic"), BE32(0), Encoding.ASCII.GetBytes("mif1heic"));
            var hdlr = Full("hdlr", 0, BE32(0), Encoding.ASCII.GetBytes("pict"), new byte[12], new byte[] { 0 });
            var iinf = Full("iinf", 0, U16(2), Infe(1, "hvc1"), Infe(2, "Exif"));
            byte[] Iloc(long exifOffset) => Full("iloc", 0, new byte[] { 0x44, 0x00 }, U16(2),
                U16(1), U16(0), U16(1), BE32(0), BE32(imageBytes),
                U16(2), U16(0), U16(1), BE32(exifOffset), BE32(exifPayload.Length));
            int metaSize = Full("meta", 0, hdlr, iinf, Iloc(0)).Length;
            long mdatPayload = ftyp.Length + metaSize + 8;
            var meta = Full("meta", 0, hdlr, iinf, Iloc(mdatPayload));
            var mdat = Box("mdat", exifPayload, new byte[imageBytes]);
            return ftyp.Concat(meta).Concat(mdat).ToArray();
        }

        private static byte[] Full(string type, int version, params byte[][] payload) =>
            Box(type, new[] { new byte[] { (byte)version, 0, 0, 0 } }.Concat(payload).ToArray());

        private static byte[] BE16(int v) => new[] { (byte)(v >> 8), (byte)v };

        /// <summary>
        /// HEIC cohérent : ftyp, meta (hdlr, pitm, iinf : image hvc1, Exif, XMP « mime »
        /// rdf+xml ; iloc en positions absolues), mdat (EXIF, XMP, image). Les éléments
        /// peuvent être mis APRÈS une boîte ajoutée (<paramref name="dataAfter"/>) pour
        /// fabriquer un fichier qu'on ne doit pas couper.
        /// </summary>
        public static byte[] HeicImage(string date = "2025:10:31 23:59:30", string xmp = null, int imageBytes = 3000)
        {
            var exif = BE32(6).Concat(Encoding.ASCII.GetBytes("Exif\0\0")).Concat(ExifTiff(date)).ToArray();
            var xmpBytes = Encoding.UTF8.GetBytes(xmp ?? "<x:xmpmeta/>");
            var image = Enumerable.Range(0, imageBytes).Select(i => (byte)(i * 13 + 1)).ToArray();
            byte[] Infe(int id, string type, string extra = "") => Full("infe", 2, BE16(id), BE16(0), Encoding.ASCII.GetBytes(type), Encoding.ASCII.GetBytes("\0" + extra));
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("heic"), BE32(0), Encoding.ASCII.GetBytes("mif1heic"));
            var hdlr = Full("hdlr", 0, BE32(0), Encoding.ASCII.GetBytes("pict"), new byte[12], new byte[] { 0 });
            var pitm = Full("pitm", 0, BE16(1));
            var iinf = Full("iinf", 0, BE16(3), Infe(1, "hvc1"), Infe(2, "Exif"), Infe(3, "mime", "application/rdf+xml\0"));
            byte[] Iloc(long at) => Full("iloc", 0, new byte[] { 0x44, 0x00 }, BE16(3),
                BE16(1), BE16(0), BE16(1), BE32(at + exif.Length + xmpBytes.Length), BE32(image.Length),
                BE16(2), BE16(0), BE16(1), BE32(at), BE32(exif.Length),
                BE16(3), BE16(0), BE16(1), BE32(at + exif.Length), BE32(xmpBytes.Length));
            int metaSize = Full("meta", 0, hdlr, pitm, iinf, Iloc(0)).Length;
            var meta = Full("meta", 0, hdlr, pitm, iinf, Iloc(ftyp.Length + metaSize + 8));
            return ftyp.Concat(meta).Concat(Box("mdat", exif, xmpBytes, image)).ToArray();
        }

        public const string GoogleMotionXmp =
            "<x:xmpmeta><rdf:Description GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\"><Container:Directory><rdf:Seq>"
            + "<rdf:li><Container:Item Item:Mime=\"image/heic\" Item:Semantic=\"Primary\"/></rdf:li>"
            + "<rdf:li><Container:Item Item:Mime=\"video/mp4\" Item:Semantic=\"MotionPhoto\" Item:Length=\"{0}\"/></rdf:li>"
            + "</rdf:Seq></Container:Directory></rdf:Description></x:xmpmeta>";

        /// <summary>Photo animée HEIC Google : la vidéo dans une boîte « mpvd » après les boîtes HEIF.</summary>
        public static byte[] HeicGoogleMotion(byte[] video)
        {
            var still = HeicImage(xmp: string.Format(GoogleMotionXmp, video.Length + 8));
            return still.Concat(Box("mpvd", video)).ToArray();
        }

        /// <summary>Remorque Samsung (SEF) après des octets quelconques : enregistrements nommés, SEFH…SEFT.</summary>
        public static byte[] WithSef(byte[] image, params (string name, int type, byte[] data)[] records)
        {
            var body = new List<byte>();
            var starts = new List<int>();
            var lengths = new List<int>();
            foreach (var (name, type, data) in records)
            {
                var n = Encoding.ASCII.GetBytes(name);
                var record = new byte[] { 0, 0, (byte)type, (byte)(type >> 8) }.Concat(LE32(n.Length)).Concat(n).Concat(data).ToArray();
                starts.Add(image.Length + body.Count);
                lengths.Add(record.Length);
                body.AddRange(record);
            }
            int sefh = image.Length + body.Count;
            var dir = new List<byte>();
            dir.AddRange(Encoding.ASCII.GetBytes("SEFH"));
            dir.AddRange(LE32(107));
            dir.AddRange(LE32(records.Length));
            for (int i = 0; i < records.Length; i++)
            {
                dir.AddRange(new byte[] { 0, 0, (byte)records[i].type, (byte)(records[i].type >> 8) });
                dir.AddRange(LE32(sefh - starts[i]));
                dir.AddRange(LE32(lengths[i]));
            }
            return image.Concat(body).Concat(dir).Concat(LE32(dir.Count)).Concat(Encoding.ASCII.GetBytes("SEFT")).ToArray();
        }

        /// <summary>
        /// Vidéo avec pistes : tkhd (taille, rotation de 90° par la matrice), mdhd et hdlr
        /// (vide/soun), métadonnées QuickTime (meta sans version, hdlr mdta, keys, ilst)
        /// comme un clip de Live Photo iPhone.
        /// </summary>
        public static byte[] Movie(double seconds, int width = 1920, int height = 1080, bool rotate90 = false, double? audio = null,
            bool appleLive = false, int mdatBytes = 2000, double? videoTrack = null, bool wideFirst = false)
        {
            byte[] Mdhd(double s) => Full("mdhd", 0, BE32(0), BE32(0), BE32(600), BE32((long)Math.Round(s * 600)), new byte[4]);
            byte[] Hdlr(string type) => Full("hdlr", 0, BE32(0), Encoding.ASCII.GetBytes(type), new byte[12], new byte[] { 0 });
            byte[] Tkhd(int w, int h)
            {
                var matrix = rotate90
                    ? new[] { 0L, 0x00010000, 0, -0x00010000 & 0xFFFFFFFF, 0, 0, 0, 0, 0x40000000 }
                    : new[] { 0x00010000L, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 };
                return Full("tkhd", 0, new byte[20], new byte[16], matrix.SelectMany(BE32).ToArray(), BE32((long)w << 16), BE32((long)h << 16));
            }
            byte[] Trak(string handler, double s, int w, int h) =>
                Box("trak", Tkhd(w, h), Box("mdia", Mdhd(s), Hdlr(handler), Box("minf")));
            var parts = new List<byte[]> { Mvhd(seconds), Trak("vide", videoTrack ?? seconds, width, height) };
            if (audio.HasValue) parts.Add(Trak("soun", audio.Value, 0, 0));
            if (appleLive)
            {
                byte[] Key(string k) => BE32(8 + k.Length).Concat(Encoding.ASCII.GetBytes("mdta")).Concat(Encoding.ASCII.GetBytes(k)).ToArray();
                var keys = Full("keys", 0, BE32(2), Key("com.apple.quicktime.content.identifier"), Key("com.apple.quicktime.location.ISO6709"));
                var ilst = Box("ilst", Box("\0\0\0\u0001", Box("data", BE32(1), BE32(0), Encoding.ASCII.GetBytes("6B1F0F47-3C51-4B0E-9C0B-1A2B3C4D5E6F"))));
                parts.Add(Box("meta", Hdlr("mdta"), keys, ilst)); // QuickTime : meta sans version
            }
            var ftyp = Box("ftyp", Encoding.ASCII.GetBytes("qt  "), BE32(0), Encoding.ASCII.GetBytes("qt  "));
            var mdat = Box("mdat", Enumerable.Range(0, mdatBytes).Select(i => (byte)(i * 11)).ToArray());
            // Certains clips iPhone commencent par « wide », sans ftyp.
            var start = wideFirst ? Box("wide") : ftyp.Concat(Box("wide")).ToArray();
            return start.Concat(mdat).Concat(Box("moov", parts.ToArray())).ToArray();
        }

        // ---------- Structures relevées sur de vrais fichiers (voir RealSamplesTests) ----------

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

        private static byte[] ContainerXmp(string extra, params (string mime, string semantic, long length, long padding)[] items) =>
            Encoding.UTF8.GetBytes("<x:xmpmeta><rdf:Description " + extra + "><Container:Directory><rdf:Seq>"
                + string.Concat(items.Select(i => $"<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Mime=\"{i.mime}\" Item:Semantic=\"{i.semantic}\" Item:Length=\"{i.length}\" Item:Padding=\"{i.padding}\"/></rdf:li>"))
                + "</rdf:Seq></Container:Directory></rdf:Description></x:xmpmeta>");

        private static byte[] WithXmpBytes(byte[] jpeg, byte[] xmp) =>
            WithApp1(jpeg, Ascii("http://ns.adobe.com/xap/1.0/\0").Concat(xmp).ToArray());

        /// <summary>
        /// Galaxy S24/S25 (One UI 6-7) : image, carte de gain JPEG, remorque SEF. S25+ : Image_UTC_Data,
        /// MCC_Data, Color_Display_P3, MotionPhoto_Data (MP4), MotionPhoto_Version « mpv3 »,
        /// MotionPhoto_AutoPlay (2e MP4). Variante relevée par ente : AutoPlay AVANT la vidéo principale.
        /// </summary>
        public static byte[] SamsungUltraHdr(byte[] primary, byte[] gainMap, byte[] video, byte[] autoPlay, bool autoPlayFirst)
        {
            var records = new List<(string, int, byte[])>();
            if (autoPlayFirst) records.Add(("MotionPhoto_AutoPlay", 0x0A33, autoPlay));
            if (!autoPlayFirst)
            {
                records.Add(("Image_UTC_Data", 0x0A01, Ascii("1771274354172")));
                records.Add(("MCC_Data", 0x0AA1, Ascii("208")));
                records.Add(("Color_Display_P3", 0x0CC1, Ascii("0")));
            }
            records.Add(("MotionPhoto_Data", 0x0A30, video));
            if (!autoPlayFirst)
            {
                records.Add(("MotionPhoto_Version", 0x0A31, Ascii("mpv3")));
                records.Add(("MotionPhoto_AutoPlay", 0x0A33, autoPlay));
            }
            else records.Add(("Image_UTC_Data", 0x0A01, Ascii("1771274354172")));
            var trailer = WithSef(new byte[0], records.ToArray());
            var xmp = ContainerXmp("hdrgm:Version=\"1.0\" GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\"",
                ("image/jpeg", "Primary", 0, 0), ("image/jpeg", "GainMap", gainMap.Length, 0), ("video/mp4", "MotionPhoto", video.Length + 100, 0));
            return WithXmpBytes(primary, xmp).Concat(gainMap).Concat(trailer).ToArray();
        }

        /// <summary>Pixel 7 Pro : bloc « aecDebug » (données de débogage, pas une image) entre l'image et le MP4.</summary>
        public static byte[] PixelWithDebugBlock(byte[] primary, byte[] video)
        {
            var debug = Ascii("aecDebug").Concat(Enumerable.Range(0, 20652).Select(i => (byte)(i * 31 + 7))).ToArray();
            var xmp = ContainerXmp("GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\" GCamera:MotionPhotoPresentationTimestampUs=\"966000\"",
                ("image/jpeg", "Primary", 0, 0), ("video/mp4", "MotionPhoto", video.Length, 0));
            return WithXmpBytes(primary, xmp).Concat(debug).Concat(video).ToArray();
        }

        /// <summary>Huawei (EMUI 9) : zéros finis par une longueur et « ctrace\0\0 », MP4, puis 40 octets ASCII « … LIVE_n ».</summary>
        public static byte[] HuaweiMovingPicture(byte[] primary, byte[] video)
        {
            var block = new byte[5420];
            var len = LE32(block.Length);
            Array.Copy(len, 0, block, block.Length - 12, 4);
            Array.Copy(Ascii("ctrace\0\0"), 0, block, block.Length - 8, 8);
            var trailer = Ascii(("540:540   ").PadRight(25) + ("LIVE_" + (video.Length)).PadRight(15));
            return primary.Concat(block).Concat(video).Concat(trailer).ToArray();
        }

        /// <summary>OnePlus 13 (« O Live Photo » v2) : image, carte de gain, 16 zéros, MP4, remorque Oplus (JSON + « jxrs » + longueur).</summary>
        public static byte[] OnePlusLive(byte[] primary, byte[] gainMap, byte[] video)
        {
            var json = Ascii("[{\"length\":24,\"name\":\"private.emptyspace\",\"offset\":190,\"version\":1}]");
            var trailer = Ascii("OnePlus 13\0\0").Concat(json).Concat(Ascii("jxrs")).Concat(LE32(json.Length)).ToArray();
            var xmp = ContainerXmp("GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\" OpCamera:MotionPhotoOwner=\"oplus\" OpCamera:OLivePhotoVersion=\"2\" OpCamera:VideoLength=\"" + video.Length + "\"",
                ("image/jpeg", "Primary", 0, 0), ("image/jpeg", "GainMap", gainMap.Length, 16), ("video/mp4", "MotionPhoto", video.Length + trailer.Length, 0));
            return WithXmpBytes(primary, xmp).Concat(gainMap).Concat(new byte[16]).Concat(video).Concat(trailer).ToArray();
        }

        /// <summary>vivo (format récent) : MP4 juste après l'image, boîte uuid « vivoMediaExtInfo » puis 15 octets magiques.</summary>
        public static byte[] VivoLive(byte[] primary, byte[] video)
        {
            var uuid = Box("uuid", Ascii("vivoMediaExtInfo"), Ascii("{\"com.android.camera.livephoto\":\"motionphoto0000000easy_share\",\"version\":2200}"));
            var magic = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x1B, 0x2A, 0x39, 0x48, 0x57, 0x66, 0x75, 0x84, 0x93, 0xA2, 0xB3 };
            var tail = video.Concat(uuid).Concat(magic).ToArray();
            var xmp = ContainerXmp("GCamera:MotionPhoto=\"1\" VCamera:VMotionPhotoVersion=\"1\"",
                ("image/jpeg", "Primary", 0, 0), ("video/mp4", "MotionPhoto", tail.Length, 0));
            return WithXmpBytes(primary, xmp).Concat(tail).ToArray();
        }

        /// <summary>Nokia 7 plus (MVIMG) : 10 255 zéros puis le MP4 ; MicroVideoOffset = taille du MP4.</summary>
        public static byte[] NokiaMicroVideo(byte[] primary, byte[] video) =>
            WithXmp(primary, $"<x:xmpmeta><rdf:Description GCamera:MicroVideo=\"1\" GCamera:MicroVideoVersion=\"1\" GCamera:MicroVideoOffset=\"{video.Length}\" GCamera:MicroVideoPresentationTimestampUs=\"-1\"/></x:xmpmeta>")
                .Concat(new byte[10255]).Concat(video).ToArray();

        /// <summary>
        /// HEIC Samsung, boîtes dans l'ordre des vrais fichiers. S22 (One UI 6) : ftyp, mdat, meta, free,
        /// mpvd (MP4), sefd (SEF dont MotionPhoto_Data n'est qu'un témoin « mpv2 »). S20 : ftyp, mdat, meta,
        /// sefd contenant tout le MP4. S21 ordinaire : ftyp, meta, sefd (sans vidéo), mdat jusqu'à la fin.
        /// </summary>
        public static byte[] SamsungHeic(string model, byte[] video = null)
        {
            var exif = BE32(6).Concat(Ascii("Exif\0\0")).Concat(ExifTiff("2025:06:01 12:00:00")).ToArray();
            var xmp = Encoding.UTF8.GetBytes(model == "S22" ? string.Format(GoogleMotionXmp, video.Length + 8) : "<x:xmpmeta/>");
            var image = Enumerable.Range(0, 4000).Select(i => (byte)(i * 17 + 3)).ToArray();
            byte[] Infe(int id, string type, string extra = "") => Full("infe", 2, BE16(id), BE16(0), Ascii(type), Ascii("\0" + extra));
            var ftyp = Box("ftyp", Ascii("heic"), BE32(0), Ascii("mif1heic"));
            var hdlr = Full("hdlr", 0, BE32(0), Ascii("pict"), new byte[12], new byte[] { 0 });
            var iinf = Full("iinf", 0, BE16(3), Infe(1, "hvc1"), Infe(2, "Exif"), Infe(3, "mime", "application/rdf+xml\0"));
            byte[] Meta(long at) => Full("meta", 0, hdlr, Full("pitm", 0, BE16(1)), iinf, Full("iloc", 0, new byte[] { 0x44, 0x00 }, BE16(3),
                BE16(1), BE16(0), BE16(1), BE32(at + exif.Length + xmp.Length), BE32(image.Length),
                BE16(2), BE16(0), BE16(1), BE32(at), BE32(exif.Length),
                BE16(3), BE16(0), BE16(1), BE32(at + exif.Length), BE32(xmp.Length)));
            var mdatPayload = exif.Concat(xmp).Concat(image).ToArray();
            var plainSef = WithSef(new byte[0], ("Image_UTC_Data", 0x0A01, Ascii("1748772000000")), ("MCC_Data", 0x0AA1, Ascii("208")));
            if (model == "S21")
            {
                var sefd = Box("sefd", plainSef);
                int metaSize = Meta(0).Length;
                var meta = Meta(ftyp.Length + metaSize + sefd.Length + 8);
                return ftyp.Concat(meta).Concat(sefd).Concat(Box("mdat", mdatPayload)).ToArray();
            }
            var head = ftyp.Concat(Box("mdat", mdatPayload)).Concat(Meta(ftyp.Length + 8)).ToArray();
            if (model == "S20")
            {
                var sef = WithSef(new byte[0], ("Image_UTC_Data", 0x0A01, Ascii("1748772000000")), ("MotionPhoto_Data", 0x0A30, video));
                return head.Concat(Box("sefd", sef)).ToArray();
            }
            var stub = WithSef(new byte[0], ("Image_UTC_Data", 0x0A01, Ascii("1748772000000")), ("MotionPhoto_Data", 0x0A30, Ascii("mpv2").Concat(new byte[8]).ToArray()));
            return head.Concat(Box("free", new byte[6062])).Concat(Box("mpvd", video)).Concat(Box("sefd", stub)).ToArray();
        }

        /// <summary>Taille des boîtes HEIF avant la vidéo (pour vérifier la coupe).</summary>
        public static int HeifPrefix(byte[] heic, string stopAt)
        {
            int pos = 0;
            while (pos + 8 <= heic.Length && Encoding.ASCII.GetString(heic, pos + 4, 4) != stopAt) pos += (int)((heic[pos] << 24) | (heic[pos + 1] << 16) | (heic[pos + 2] << 8) | heic[pos + 3]);
            return pos;
        }

        /// <summary>Image de test texturée (dégradé, formes, bruit), reproductible par sa graine.</summary>
        public static System.Drawing.Bitmap Scene(int seed, int width, int height)
        {
            var random = new Random(seed);
            var bitmap = new System.Drawing.Bitmap(width, height);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                using (var gradient = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)),
                    System.Drawing.Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)), random.Next(360)))
                    g.FillRectangle(gradient, 0, 0, width, height);
                for (int k = 0; k < 40; k++)
                {
                    using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, random.Next(256), random.Next(256), random.Next(256))))
                    {
                        int w = random.Next(width / 10, width / 3), h = random.Next(height / 10, height / 3);
                        int x = random.Next(-w / 2, width), y = random.Next(-h / 2, height);
                        if (k % 2 == 0) g.FillEllipse(brush, x, y, w, h);
                        else g.FillRectangle(brush, x, y, w, h);
                    }
                }
            }
            return bitmap;
        }

        public static byte[] Encode(System.Drawing.Image image, long quality = 90)
        {
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using (var parameters = new System.Drawing.Imaging.EncoderParameters(1))
            using (var ms = new System.IO.MemoryStream())
            {
                parameters.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                image.Save(ms, codec, parameters);
                return ms.ToArray();
            }
        }

        public static System.Drawing.Bitmap Resized(System.Drawing.Image image, int width, int height)
        {
            var bitmap = new System.Drawing.Bitmap(width, height);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, 0, 0, width, height);
            }
            return bitmap;
        }

        /// <summary>
        /// Photo d'appareil : pixels stockés tels que le capteur les voit (orientation
        /// EXIF à appliquer), EXIF avec date, dimensions et vignette JPEG (IFD1).
        /// </summary>
        public static byte[] CameraJpeg(System.Drawing.Bitmap stored, string date, int orientation, byte[] thumbnail)
        {
            var t = new List<byte>();
            void U16(int v) { t.Add((byte)v); t.Add((byte)(v >> 8)); }
            void U32(long v) { t.AddRange(LE32(v)); }
            void Entry(int tag, int type, int count, long value)
            {
                U16(tag); U16(type); U32(count);
                if (type == 3) { U16((int)value); U16(0); } else U32(value);
            }
            int ifd0 = 8, ifd0Size = 2 + 2 * 12 + 4;
            int exif = ifd0 + ifd0Size, exifSize = 2 + 3 * 12 + 4;
            int ifd1 = exif + exifSize, ifd1Size = 2 + 3 * 12 + 4;
            int dateAt = ifd1 + ifd1Size, thumbAt = dateAt + 20;
            t.AddRange(new byte[] { 0x49, 0x49 });
            U16(42);
            U32(8);
            U16(2);
            Entry(0x0112, 3, 1, orientation);
            Entry(0x8769, 4, 1, exif);
            U32(ifd1);
            U16(3);
            Entry(0x9003, 2, 20, dateAt);
            Entry(0xA002, 4, 1, stored.Width);
            Entry(0xA003, 4, 1, stored.Height);
            U32(0);
            U16(3);
            Entry(0x0103, 3, 1, 6);
            Entry(0x0201, 4, 1, thumbAt);
            Entry(0x0202, 4, 1, thumbnail.Length);
            U32(0);
            t.AddRange(Encoding.ASCII.GetBytes(date + "\0"));
            t.AddRange(thumbnail);
            return WithApp1(Encode(stored, 92), Encoding.ASCII.GetBytes("Exif\0\0").Concat(t).ToArray());
        }
    }
}
