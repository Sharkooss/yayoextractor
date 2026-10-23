using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public class ExifAndDestinationTests
    {
        /// <summary>Début de JPEG minimal : SOI, APP1 Exif (IFD0 → ExifIFD), SOS.</summary>
        internal static byte[] JpegWithExif(string dateOriginal, bool bigEndian, string ifd0Date = null)
        {
            var tiff = new List<byte>();
            void U16(int v) { if (bigEndian) { tiff.Add((byte)(v >> 8)); tiff.Add((byte)v); } else { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); } }
            void U32(int v) { if (bigEndian) { U16(v >> 16); U16(v & 0xFFFF); } else { U16(v & 0xFFFF); U16(v >> 16); } }

            tiff.AddRange(bigEndian ? new byte[] { 0x4D, 0x4D } : new byte[] { 0x49, 0x49 });
            U16(42);
            U32(8);
            int ifd0Entries = ifd0Date == null ? 1 : 2;
            int ifd0Size = 2 + ifd0Entries * 12 + 4;
            int exifIfdOffset = 8 + ifd0Size;
            int exifIfdSize = 2 + 12 + 4;
            int dataOffset = exifIfdOffset + exifIfdSize;
            // IFD0
            U16(ifd0Entries);
            if (ifd0Date != null) { U16(0x0132); U16(2); U32(20); U32(dataOffset + 20); }
            U16(0x8769); U16(4); U32(1); U32(exifIfdOffset);
            U32(0);
            // ExifIFD
            U16(1);
            U16(0x9003); U16(2); U32(20); U32(dataOffset);
            U32(0);
            tiff.AddRange(Encoding.ASCII.GetBytes((dateOriginal ?? "0000:00:00 00:00:00") + "\0"));
            if (ifd0Date != null) tiff.AddRange(Encoding.ASCII.GetBytes(ifd0Date + "\0"));

            var app1 = new List<byte>();
            app1.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
            app1.AddRange(tiff);
            var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
            jpeg.AddRange(new byte[14]); // APP0 JFIF factice, à sauter
            jpeg.AddRange(new byte[] { 0xFF, 0xE1, (byte)((app1.Count + 2) >> 8), (byte)(app1.Count + 2) });
            jpeg.AddRange(app1);
            jpeg.AddRange(new byte[] { 0xFF, 0xDA, 0x00, 0x02 });
            return jpeg.ToArray();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Lit_DateTimeOriginal_dans_les_deux_boutismes(bool bigEndian)
        {
            var head = JpegWithExif("2026:10:21 14:03:22", bigEndian);
            Assert.True(ExifDate.TryRead(head, out var date));
            Assert.Equal(new DateTime(2026, 10, 21, 14, 3, 22), date);
        }

        [Fact]
        public void Se_rabat_sur_DateTime_d_IFD0_si_DateTimeOriginal_est_vide()
        {
            var head = JpegWithExif(null, false, "2025:03:09 08:00:00");
            Assert.True(ExifDate.TryRead(head, out var date));
            Assert.Equal(new DateTime(2025, 3, 9, 8, 0, 0), date);
        }

        [Fact]
        public void Refuse_ce_qui_n_est_pas_un_jpeg_exif()
        {
            Assert.False(ExifDate.TryRead(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, out _));
            Assert.False(ExifDate.TryRead(new byte[] { 0xFF, 0xD8, 0xFF, 0xDA, 0, 2 }, out _));
            Assert.False(ExifDate.TryRead(new byte[0], out _));
        }

        [Fact]
        public void Resiste_a_un_debut_tronque()
        {
            var head = JpegWithExif("2026:10:21 14:03:22", false);
            for (int cut = 0; cut < head.Length; cut++)
            {
                var partial = new byte[cut];
                Array.Copy(head, partial, cut);
                ExifDate.TryRead(partial, out _); // ne doit jamais lever
            }
        }

        [Theory]
        [InlineData(1, "01 - Janvier")]
        [InlineData(2, "02 - Février")]
        [InlineData(8, "08 - Août")]
        [InlineData(10, "10 - Octobre")]
        [InlineData(12, "12 - Décembre")]
        public void Range_par_annee_et_mois(int month, string expected)
        {
            var folder = Destination.Folder(@"E:\", new DateTime(2026, month, 5));
            Assert.Equal(Path.Combine(@"E:\", "Photos du téléphone", "2026", expected), folder);
        }

        [Fact]
        public void Nom_libre_ajoute_un_numero()
        {
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "IMG_1.jpg", "IMG_1 (2).jpg" };
            Assert.Equal("IMG_2.jpg", Destination.FreeName("IMG_2.jpg", taken.Contains));
            Assert.Equal("IMG_1 (3).jpg", Destination.FreeName("IMG_1.jpg", taken.Contains));
            // Windows ne distingue pas la casse : img_1.JPG est déjà pris.
            Assert.Equal("img_1 (3).JPG", Destination.FreeName("img_1.JPG", taken.Contains));
        }
    }
}
