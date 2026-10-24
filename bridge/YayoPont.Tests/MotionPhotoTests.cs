using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public class MotionPhotoTests
    {
        private static byte[] Strip(byte[] data)
        {
            var copy = (byte[])data.Clone();
            int length = MotionPhoto.Strip(copy, copy.Length);
            return copy.Take(length).ToArray();
        }

        private static void AssertDecodable(byte[] jpeg)
        {
            using (var image = Image.FromStream(new MemoryStream(jpeg))) Assert.True(image.Width > 0);
        }

        [Fact]
        public void Fin_de_l_image_d_un_vrai_jpeg()
        {
            var jpeg = MediaFactory.Jpeg(200, 100);
            Assert.Equal(jpeg.Length, MotionPhoto.ImageEnd(jpeg, jpeg.Length));
            Assert.Equal(-1, MotionPhoto.ImageEnd(jpeg, jpeg.Length - 2)); // FFD9 manquant
        }

        [Fact]
        public void Jpeg_progressif_bourrage_et_rst()
        {
            // SOI, DQT, SOS + données avec FF00 et RST0, DHT entre deux scans, SOS, données, EOI.
            var b = new byte[] { 0xFF, 0xD8, 0xFF, 0xDB, 0, 4, 1, 2,
                0xFF, 0xDA, 0, 3, 9, 0x11, 0xFF, 0x00, 0x22, 0xFF, 0xD0, 0x33,
                0xFF, 0xC4, 0, 3, 7,
                0xFF, 0xDA, 0, 2, 0x44, 0xFF, 0xFF, 0xD9, 0xAA, 0xBB };
            Assert.Equal(b.Length - 2, MotionPhoto.ImageEnd(b, b.Length));
        }

        [Fact]
        public void Photo_ordinaire_inchangee()
        {
            var jpeg = MediaFactory.Jpeg();
            Assert.Equal(jpeg, Strip(jpeg));
            var samsung = MediaFactory.SamsungPlain(jpeg);
            Assert.Equal(samsung, Strip(samsung));
            Assert.Equal(-1, MotionPhoto.VideoStart(samsung, samsung.Length));
        }

        [Fact]
        public void Samsung_coupe_avant_l_enregistrement_MotionPhoto_Data()
        {
            var jpeg = MediaFactory.Jpeg();
            var motion = MediaFactory.SamsungMotion(jpeg, MediaFactory.Mp4());
            var stripped = Strip(motion);
            Assert.Equal(jpeg, stripped);
            AssertDecodable(stripped);
        }

        [Fact]
        public void Google_motion_photo_coupe_a_la_video_et_neutralise_le_xmp()
        {
            var video = MediaFactory.Mp4();
            var motion = MediaFactory.GoogleMotion(video);
            Assert.True(MotionPhoto.HeadSaysMotion(motion.Take(4096).ToArray()));
            var stripped = Strip(motion);
            Assert.Equal(motion.Length - video.Length, stripped.Length);
            var text = Encoding.ASCII.GetString(stripped);
            Assert.Contains("GCamera:MotionPhoto=\"0\"", text);
            Assert.Contains("GCamera:MotionPhotoVersion=\"1\"", text);
            Assert.DoesNotContain("Item:Length=\"" + video.Length + "\"", text);
            Assert.False(MotionPhoto.HeadSaysMotion(stripped));
            AssertDecodable(stripped);
        }

        [Fact]
        public void Carte_de_gain_hdr_placee_avant_la_video_est_gardee()
        {
            var video = MediaFactory.Mp4();
            var gainMap = MediaFactory.Jpeg(16, 16);
            var motion = MediaFactory.GoogleMotion(video, gainMap);
            var stripped = Strip(motion);
            Assert.Equal(motion.Length - video.Length, stripped.Length);
            Assert.Equal(gainMap, stripped.Skip(stripped.Length - gainMap.Length).ToArray());
        }

        [Fact]
        public void Ancien_format_MicroVideo()
        {
            var video = MediaFactory.Mp4();
            var motion = MediaFactory.GoogleMotion(video, microVideo: true);
            var stripped = Strip(motion);
            Assert.Equal(motion.Length - video.Length, stripped.Length);
            Assert.Contains("GCamera:MicroVideo=\"0\"", Encoding.ASCII.GetString(stripped));
        }

        [Fact]
        public void Video_collee_sans_marqueur_reperee_par_ftyp()
        {
            var jpeg = MediaFactory.Jpeg();
            var motion = jpeg.Concat(MediaFactory.Mp4()).ToArray();
            Assert.Equal(jpeg, Strip(motion));
        }

        [Fact]
        public void Pas_un_jpeg_valide_rien_n_est_touche()
        {
            var broken = MediaFactory.Jpeg().Take(300).Concat(MediaFactory.Mp4()).ToArray();
            Assert.Equal(broken, Strip(broken));
            var notJpeg = MediaFactory.Mp4();
            Assert.Equal(notJpeg, Strip(notJpeg));
        }

        [Fact]
        public void Detection_sur_disque_sans_tout_lire()
        {
            var dir = Path.Combine(Path.GetTempPath(), "yayo-motion-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                void Write(string name, byte[] data) => File.WriteAllBytes(Path.Combine(dir, name), data);
                Write("samsung.jpg", MediaFactory.SamsungMotion(MediaFactory.Jpeg(), MediaFactory.Mp4()));
                Write("google.jpg", MediaFactory.GoogleMotion(MediaFactory.Mp4()));
                Write("plain.jpg", MediaFactory.SamsungPlain(MediaFactory.Jpeg()));
                Assert.True(MotionPhoto.LooksAnimated(Path.Combine(dir, "samsung.jpg")));
                Assert.True(MotionPhoto.LooksAnimated(Path.Combine(dir, "google.jpg")));
                Assert.False(MotionPhoto.LooksAnimated(Path.Combine(dir, "plain.jpg")));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
