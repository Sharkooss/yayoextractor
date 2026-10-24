using System;
using System.IO;
using System.Linq;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public class ExifInfoTests
    {
        [Fact]
        public void Jpeg_date_et_gps_nord_est()
        {
            var info = ExifInfo.Read(MediaFactory.JpegWithExif("2026:07:14 10:30:00", 45.764, 4.8357));
            Assert.Equal(new DateTime(2026, 7, 14, 10, 30, 0), info.Date);
            Assert.Equal(45.764, info.Lat.Value, 4);
            Assert.Equal(4.8357, info.Lon.Value, 4);
        }

        [Fact]
        public void Jpeg_gps_sud_ouest()
        {
            var info = ExifInfo.Read(MediaFactory.JpegWithExif("2025:01:02 03:04:05", -22.9068, -43.1729));
            Assert.Equal(-22.9068, info.Lat.Value, 4);
            Assert.Equal(-43.1729, info.Lon.Value, 4);
        }

        [Fact]
        public void Sans_gps_date_seule()
        {
            var info = ExifInfo.Read(MediaFactory.JpegWithExif("2025:01:02 03:04:05"));
            Assert.NotNull(info.Date);
            Assert.Null(info.Lat);
            Assert.Null(info.Lon);
        }

        [Fact]
        public void Heic_exif_dans_le_conteneur()
        {
            var heic = MediaFactory.Heic("2024:12:25 18:00:00", 48.8584, 2.2945);
            var info = ExifInfo.Read(heic);
            Assert.Equal(new DateTime(2024, 12, 25, 18, 0, 0), info.Date);
            Assert.Equal(48.8584, info.Lat.Value, 4);
            Assert.Equal(2.2945, info.Lon.Value, 4);
            Assert.True(ExifDate.TryRead(heic, out var date));
            Assert.Equal(2024, date.Year);
        }

        [Fact]
        public void Heic_tronque_ou_exif_hors_de_la_tete_ne_leve_pas()
        {
            var heic = MediaFactory.Heic("2024:12:25 18:00:00", 48.8584, 2.2945);
            for (int cut = 0; cut < heic.Length; cut += 7)
                ExifInfo.Read(heic.Take(cut).ToArray());
            var jpeg = MediaFactory.JpegWithExif("2026:07:14 10:30:00", 45.764, 4.8357);
            for (int cut = 0; cut < 400; cut++)
                ExifInfo.Read(jpeg.Take(cut).ToArray());
        }

        [Fact]
        public void Tete_jpeg_lue_jusqu_au_debut_de_l_image()
        {
            var jpeg = MediaFactory.JpegWithExif("2026:07:14 10:30:00").Concat(new byte[500 * 1024]).ToArray();
            var head = ExifInfo.ReadHead(new MemoryStream(jpeg), 256 * 1024);
            Assert.Equal(64 * 1024, head.Length); // l'en-tête tient dans le premier bloc
            Assert.NotNull(ExifInfo.Read(head).Date);

            var heic = MediaFactory.Heic("2024:12:25 18:00:00");
            Assert.Equal(heic.Length, ExifInfo.ReadHead(new MemoryStream(heic), 256 * 1024).Length);
        }
    }
}
