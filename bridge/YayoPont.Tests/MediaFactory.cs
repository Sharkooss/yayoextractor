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
    }
}
