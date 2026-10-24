using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    /// <summary>Photos animées de toutes les marques, JPEG et HEIC, et détection rapide sur la clé.</summary>
    public sealed class MotionPhotoBrandsTests : System.IDisposable
    {
        private readonly string dir = Path.Combine(Path.GetTempPath(), "yayo-marques-" + System.Guid.NewGuid().ToString("N"));

        public MotionPhotoBrandsTests() => Directory.CreateDirectory(dir);

        public void Dispose() => Directory.Delete(dir, true);

        private static byte[] Strip(byte[] data)
        {
            var copy = (byte[])data.Clone();
            int length = MotionPhoto.Strip(copy, copy.Length);
            return copy.Take(length).ToArray();
        }

        private bool Looks(byte[] data)
        {
            var path = Path.Combine(dir, System.Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(path, data);
            return MotionPhoto.LooksAnimated(path);
        }

        private static void AssertDecodable(byte[] jpeg)
        {
            using (var image = Image.FromStream(new MemoryStream(jpeg))) Assert.True(image.Width > 0);
        }

        [Fact]
        public void Mp4_colle_avec_en_tete_maison_coupe_a_la_fin_de_l_image()
        {
            var jpeg = MediaFactory.Jpeg();
            var header = Encoding.ASCII.GetBytes("LIVE_HEADER\0\0\0\u0010vendor-data-0123");
            var motion = jpeg.Concat(header).Concat(MediaFactory.Mp4()).ToArray();
            Assert.True(Looks(motion));
            Assert.Equal(jpeg, Strip(motion));
        }

        [Fact]
        public void Samsung_avec_d_autres_enregistrements_coupe_a_la_fin_de_l_image()
        {
            var jpeg = MediaFactory.Jpeg();
            var motion = MediaFactory.WithSef(jpeg,
                ("Image_UTC_Data", 0x0A01, Encoding.ASCII.GetBytes("1700000000000")),
                ("MotionPhoto_Data", 0x0A30, MediaFactory.Mp4()),
                ("MotionPhoto_Version", 0x0A31, Encoding.ASCII.GetBytes("1.0")));
            Assert.True(Looks(motion));
            Assert.Equal(jpeg, Strip(motion));
        }

        [Theory]
        [InlineData("MotionPhoto_Version")]
        [InlineData("mpv2")]
        [InlineData("MotionPhoto_AutoPlay")]
        public void Remorque_samsung_qui_annonce_une_photo_animee_est_lue_en_entier(string record)
        {
            var file = MediaFactory.WithSef(MediaFactory.Jpeg(), ("Image_UTC_Data", 0x0A01, new byte[13]), (record, 0x0A31, new byte[4]));
            Assert.True(Looks(file));
            Assert.Equal(file, Strip(file)); // pas de vidéo : Strip ne touche à rien
        }

        [Fact]
        public void Detection_rapide_photos_ordinaires_non_lues()
        {
            Assert.False(Looks(MediaFactory.Jpeg()));                              // finit par FFD9
            Assert.False(Looks(MediaFactory.SamsungPlain(MediaFactory.Jpeg())));   // remorque sans photo animée
            Assert.False(Looks(MediaFactory.HeicImage()));                         // boîtes HEIF exactes
            Assert.False(Looks(MediaFactory.Mp4()));                               // pas une photo
        }

        [Fact]
        public void Remorque_samsung_illisible_lue_en_entier_et_laissee_intacte()
        {
            var file = MediaFactory.SamsungPlain(MediaFactory.Jpeg());
            file[file.Length - 20] ^= 0x55; // entrée du répertoire SEF abîmée
            var broken = MediaFactory.Jpeg().Concat(Encoding.ASCII.GetBytes("garbage")).Concat(MediaFactory.LE32(500)).Concat(Encoding.ASCII.GetBytes("SEFT")).ToArray();
            Assert.True(Looks(broken));
            Assert.Equal(broken, Strip(broken));
            Assert.Equal(file, Strip(file));
        }

        [Fact]
        public void Heic_google_mpvd_coupe_apres_les_boites_heif()
        {
            var video = MediaFactory.Mp4();
            var still = MediaFactory.HeicImage(xmp: string.Format(MediaFactory.GoogleMotionXmp, video.Length + 8));
            var motion = MediaFactory.HeicGoogleMotion(video);
            Assert.True(Looks(motion));
            var stripped = Strip(motion);
            Assert.Equal(still.Length, stripped.Length);
            var text = Encoding.ASCII.GetString(stripped);
            Assert.Contains("GCamera:MotionPhoto=\"0\"", text);
            Assert.DoesNotContain("Item:Length=\"" + (video.Length + 8) + "\"", text);
            Assert.False(Looks(stripped));
            Assert.Equal(new System.DateTime(2025, 10, 31, 23, 59, 30), ExifInfo.Read(stripped).Date); // EXIF toujours lisible
        }

        [Fact]
        public void Heic_samsung_remorque_sef_coupee()
        {
            var still = MediaFactory.HeicImage();
            var motion = MediaFactory.WithSef(still, ("Image_UTC_Data", 0x0A01, new byte[13]), ("MotionPhoto_Data", 0x0A30, MediaFactory.Mp4()));
            Assert.True(Looks(motion));
            Assert.Equal(still, Strip(motion));
        }

        [Fact]
        public void Heic_dont_les_donnees_sont_apres_la_coupe_reste_intact()
        {
            // Un élément (iloc) pointe au-delà de la fin des boîtes HEIF : couper le rendrait invalide.
            var still = MediaFactory.HeicImage(imageBytes: 100);
            var motion = still.Concat(MediaFactory.Box("mpvd", MediaFactory.Mp4())).ToArray();
            // On allonge l'étendue de l'image principale jusque dans la vidéo.
            int iloc = MotionPhoto.IndexOf(motion, Encoding.ASCII.GetBytes("iloc"), 0, motion.Length);
            int lengthAt = iloc + 4 + 4 + 2 + 2 + 2 + 2 + 2 + 4;
            var longer = MediaFactory.BE32(5000);
            for (int i = 0; i < 4; i++) motion[lengthAt + i] = longer[i];
            Assert.Equal(motion, Strip(motion));
            // Et un fichier qui n'est pas un HEIF n'est jamais touché.
            var notHeif = MediaFactory.Mp4().Concat(MediaFactory.Mp4()).ToArray();
            Assert.Equal(notHeif, Strip(notHeif));
        }

        [Fact]
        public void Heic_sans_video_jamais_modifie()
        {
            var still = MediaFactory.HeicImage(xmp: "<x:xmpmeta GCamera:MotionPhoto=\"1\"/>");
            Assert.Equal(still, Strip(still));
            var padded = still.Concat(new byte[64]).ToArray(); // octets en trop, sans vidéo
            Assert.True(Looks(padded));
            Assert.Equal(padded, Strip(padded));
        }

        // ---------- Structures relevées sur de vrais fichiers ----------

        private static byte[] Primary() => MediaFactory.JpegWithExif("2025:06:01 12:00:00");

        private static int PrimaryEnd(byte[] file) => MotionPhoto.ImageEnd(file, file.Length);

        [Theory]
        [InlineData(false)] // Galaxy S25+ : vidéo principale puis clip AutoPlay
        [InlineData(true)]  // relevé par ente : clip AutoPlay AVANT la vidéo principale
        public void Samsung_ultra_hdr_garde_la_carte_de_gain_et_retire_les_deux_videos(bool autoPlayFirst)
        {
            var gainMap = MediaFactory.Jpeg(16, 12);
            var file = MediaFactory.SamsungUltraHdr(Primary(), gainMap, MediaFactory.Mp4(3.1, mdatBytes: 9000), MediaFactory.Mp4(1, mdatBytes: 3000), autoPlayFirst);
            Assert.True(Looks(file));
            var stripped = Strip(file);
            Assert.Equal(-1, MotionPhoto.IndexOf(stripped, Encoding.ASCII.GetBytes("ftyp"), 0, stripped.Length));
            int end = PrimaryEnd(file);
            Assert.Equal(gainMap, stripped.Skip(end).Take(gainMap.Length).ToArray());
            if (autoPlayFirst) Assert.Equal(end + gainMap.Length, stripped.Length);
            Assert.Contains("GCamera:MotionPhoto=\"0\"", Encoding.ASCII.GetString(stripped));
            AssertDecodable(stripped);
        }

        [Fact]
        public void Pixel_bloc_de_debogage_avant_la_video_coupe_a_la_fin_de_l_image()
        {
            var primary = Primary();
            var file = MediaFactory.PixelWithDebugBlock(primary, MediaFactory.Mp4());
            Assert.True(Looks(file));
            var stripped = Strip(file);
            Assert.Equal(PrimaryEnd(file), stripped.Length);
            Assert.Contains("GCamera:MotionPhoto=\"0\"", Encoding.ASCII.GetString(stripped));
        }

        [Fact]
        public void Huawei_ctrace_et_remorque_live()
        {
            var primary = Primary();
            var file = MediaFactory.HuaweiMovingPicture(primary, MediaFactory.Mp4());
            Assert.True(Looks(file)); // finit par des espaces, pas par FFD9
            Assert.Equal(primary, Strip(file));
            // Huawei récent (HarmonyOS) : JPEG avec un gros bloc « ctrace » mais sans MP4 : jamais modifié.
            var noVideo = primary.Concat(new byte[3000]).Concat(Encoding.ASCII.GetBytes("ctrace\0\0")).ToArray();
            Assert.True(Looks(noVideo));
            Assert.Equal(noVideo, Strip(noVideo));
        }

        [Fact]
        public void OnePlus_carte_de_gain_video_et_remorque_oplus()
        {
            var gainMap = MediaFactory.Jpeg(16, 12);
            var file = MediaFactory.OnePlusLive(Primary(), gainMap, MediaFactory.Mp4());
            Assert.True(Looks(file));
            var stripped = Strip(file);
            Assert.Equal(PrimaryEnd(file) + gainMap.Length + 16, stripped.Length); // carte de gain et ses 16 zéros gardés
            Assert.DoesNotContain("jxrs", Encoding.ASCII.GetString(stripped));
        }

        [Fact]
        public void Vivo_xiaomi_nokia_mp4_colle_a_l_image()
        {
            var primary = Primary();
            foreach (var file in new[]
            {
                MediaFactory.VivoLive(primary, MediaFactory.Mp4()),
                MediaFactory.NokiaMicroVideo(primary, MediaFactory.Mp4()),
                MediaFactory.GoogleMotion(MediaFactory.Mp4(), microVideo: true), // Xiaomi ancien : MicroVideo, MP4 juste après l'image
            })
            {
                Assert.True(Looks(file));
                var stripped = Strip(file);
                Assert.Equal(PrimaryEnd(file), stripped.Length);
                Assert.Equal(-1, MotionPhoto.IndexOf(stripped, Encoding.ASCII.GetBytes("ftyp"), 0, stripped.Length));
            }
        }

        [Fact]
        public void Heic_samsung_s22_mpvd_puis_sefd()
        {
            var file = MediaFactory.SamsungHeic("S22", MediaFactory.Mp4());
            Assert.True(Looks(file));
            var stripped = Strip(file);
            Assert.Equal(MediaFactory.HeifPrefix(file, "mpvd"), stripped.Length);
            Assert.Contains("GCamera:MotionPhoto=\"0\"", Encoding.ASCII.GetString(stripped));
            Assert.False(Looks(stripped));
            Assert.Equal(new System.DateTime(2025, 6, 1, 12, 0, 0), ExifInfo.Read(stripped).Date);
        }

        [Fact]
        public void Heic_samsung_s20_video_dans_sefd()
        {
            var file = MediaFactory.SamsungHeic("S20", MediaFactory.Mp4());
            Assert.True(Looks(file));
            Assert.Equal(MediaFactory.HeifPrefix(file, "sefd"), Strip(file).Length);
        }

        [Fact]
        public void Heic_samsung_s21_ordinaire_sefd_avant_mdat_jamais_modifie()
        {
            var file = MediaFactory.SamsungHeic("S21");
            Assert.Equal(file, Strip(file));
        }

        [Fact]
        public void Clip_iphone_qui_commence_par_wide()
        {
            var clip = MediaFactory.Movie(2.3, 1920, 1440, appleLive: true, wideFirst: true);
            var path = Path.Combine(dir, "IMG_0019.MOV");
            File.WriteAllBytes(path, clip);
            Assert.True(Mp4Info.IsComplete(path));
            var facts = Mp4Info.Facts(Mp4Info.ReadMoov(path));
            Assert.Equal(2.3, facts.Duration.Value, 2);
            Assert.True(facts.AppleLive);
        }

        [Fact]
        public void Jpeg_jamais_rendu_invalide()
        {
            var jpeg = MediaFactory.Jpeg();
            // Image principale sans fin (FFD9) : rien n'est coupé.
            var truncated = jpeg.Take(jpeg.Length - 2).Concat(MediaFactory.Mp4()).ToArray();
            Assert.Equal(truncated, Strip(truncated));
            // Seule une image complète reste : décodable.
            var motion = MediaFactory.SamsungMotion(jpeg, MediaFactory.Mp4());
            AssertDecodable(Strip(motion));
        }
    }
}
