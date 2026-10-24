using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    /// <summary>Rangement 1.1.1 : clips de toutes les marques, copies « Sent », empreinte visuelle, métadonnées vidéo.</summary>
    public sealed class RepairV2Tests : IDisposable
    {
        private readonly string drive = Path.Combine(Path.GetTempPath(), "yayo-range2-" + Guid.NewGuid().ToString("N"));
        private string Root => Path.Combine(drive, "Photos du téléphone");
        private const string SentFolder = "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent/";
        private const string SentVideos = "Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/Sent/";

        public void Dispose()
        {
            if (!Directory.Exists(drive)) return;
            foreach (var file in Directory.GetFiles(drive, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(drive, true);
        }

        private string Write(string relative, byte[] data, DateTime? date = null)
        {
            var path = Path.Combine(Root, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            if (date.HasValue) File.SetLastWriteTime(path, date.Value);
            return path;
        }

        private RepairReport Run(IList<PhoneFile> phone = null) =>
            KeyRepair.Run(Root, new RepairContext { Phone = phone ?? new List<PhoneFile>() }, CancellationToken.None);

        internal static PhoneFile Phone(string relative, long size, DateTime date, bool hidden = false, bool live = false)
        {
            var name = relative.Substring(relative.LastIndexOf('/') + 1);
            return new PhoneFile
            {
                Id = relative, Name = name, RelativePath = relative, Size = size, Date = date,
                Kind = MediaRules.Kind(name), Source = MediaRules.Source(relative), Hidden = hidden, Live = live,
            };
        }

        // ---------- Métadonnées vidéo ----------

        [Fact]
        public void Metadonnees_video_taille_rotation_pistes_et_live_photo()
        {
            var facts = Mp4Info.Facts(Mp4Info.ReadMoov(new MemoryStream(MediaFactory.Movie(12.5, 1920, 1080, rotate90: true, audio: 12.48))));
            Assert.Equal(12.5, facts.Duration.Value, 3);
            Assert.Equal(1080, facts.Width);
            Assert.Equal(1920, facts.Height);
            Assert.Equal(12.5, facts.VideoDuration.Value, 2);
            Assert.Equal(12.48, facts.AudioDuration.Value, 2);
            Assert.False(facts.AppleLive);
            var live = Mp4Info.Facts(Mp4Info.ReadMoov(new MemoryStream(MediaFactory.Movie(2.9, appleLive: true))));
            Assert.True(live.AppleLive);
            Assert.Null(live.AudioDuration);
        }

        // ---------- Clips de photos animées ----------

        [Fact]
        public void Clip_iphone_range_dans_le_mois_voisin_est_retire()
        {
            Write("2025/10 - Octobre/IMG_7.HEIC", MediaFactory.HeicImage(), new DateTime(2025, 10, 31, 23, 59, 30));
            Write("2025/11 - Novembre/IMG_7.MOV", MediaFactory.Movie(2.8), new DateTime(2025, 11, 1, 0, 0, 20));
            var report = Run();
            Assert.Equal(1, report.LiveRemoved);
            Assert.False(Directory.Exists(Path.Combine(Root, "2025", "11 - Novembre")));
            Assert.True(File.Exists(Path.Combine(Root, "2025", "10 - Octobre", "IMG_7.HEIC")));
        }

        [Fact]
        public void Clip_iphone_reconnu_a_ses_metadonnees_meme_loin_de_sa_photo()
        {
            Write("2024/IMG_E8.HEIC", MediaFactory.HeicImage(), new DateTime(2024, 3, 1, 10, 0, 0));
            Write("2025/IMG_E8.MOV", MediaFactory.Movie(2.5, appleLive: true), new DateTime(2025, 6, 1, 10, 0, 0));
            Write("2025/IMG_9.MOV", MediaFactory.Movie(2.5, appleLive: true, mdatBytes: 2100), new DateTime(2025, 6, 1, 10, 0, 0)); // photo sur le téléphone
            Write("2025/IMG_10.MOV", MediaFactory.Movie(2.5, appleLive: true, mdatBytes: 2200), new DateTime(2025, 6, 1, 10, 0, 0)); // photo nulle part : gardé
            var phone = new List<PhoneFile> { Phone("DCIM/100APPLE/IMG_9.HEIC", 1000, new DateTime(2025, 6, 1, 10, 0, 0)) };
            var report = Run(phone);
            Assert.Equal(2, report.LiveRemoved);
            Assert.True(File.Exists(Path.Combine(Root, "2025", "IMG_10.MOV")));
        }

        [Fact]
        public void Vraies_videos_gardees()
        {
            // Même nom qu'une photo, ailleurs, une heure d'écart, sans métadonnées Live Photo.
            Write("2025/a/IMG_1.jpg", MediaFactory.Jpeg(), new DateTime(2025, 1, 1, 10, 0, 0));
            Write("2025/b/IMG_1.mp4", MediaFactory.Movie(2), new DateTime(2025, 1, 1, 11, 0, 0));
            // Live Photo de plus de 4 s : gardée.
            Write("2025/a/IMG_2.HEIC", MediaFactory.HeicImage(), new DateTime(2025, 1, 1, 10, 0, 0));
            Write("2025/a/IMG_2.MOV", MediaFactory.Movie(9, appleLive: true), new DateTime(2025, 1, 1, 10, 0, 0));
            var report = Run();
            Assert.Equal(0, report.LiveRemoved);
        }

        [Fact]
        public void Clip_repere_sur_le_telephone_retire_meme_sans_sa_photo_sur_la_cle()
        {
            Write("2025/20250101_1.mp4", MediaFactory.Mp4(2.5), new DateTime(2025, 1, 1, 10, 0, 0));
            var phone = new List<PhoneFile>
            {
                Phone("DCIM/Camera/20250101_1.jpg", 4000, new DateTime(2025, 1, 1, 10, 0, 0)),
                Phone("DCIM/Camera/20250101_1.mp4", 3000, new DateTime(2025, 1, 1, 10, 0, 0), live: true),
            };
            Assert.Equal(1, Run(phone).LiveRemoved);
        }

        // ---------- Empreinte visuelle ----------

        private static VisualPrint Print(Image image) => VisualPrint.FromImage(image, 1);

        private static VisualPrint Decode(byte[] jpeg, int orientation = 1) => VisualPrint.FromBytes(jpeg, 0, jpeg.Length, orientation);

        [Fact]
        public void Empreinte_resiste_a_la_recompression_et_distingue_les_photos()
        {
            using (var scene = MediaFactory.Scene(1, 1200, 900))
            using (var small = MediaFactory.Resized(scene, 640, 480))
            using (var other = MediaFactory.Scene(2, 1200, 900))
            using (var changed = MediaFactory.Scene(1, 1200, 900))
            {
                using (var g = Graphics.FromImage(changed)) g.FillEllipse(Brushes.Black, 300, 200, 500, 400); // même scène, autre contenu
                var original = Decode(MediaFactory.Encode(scene, 92));
                var copy = Decode(MediaFactory.Encode(small, 55));
                Assert.True(copy.Resembles(original));
                Assert.True(copy.Confirms(original));
                Assert.False(Print(other).Confirms(original));
                Assert.False(Print(changed).Confirms(original));
            }
        }

        [Fact]
        public void Empreinte_applique_l_orientation_exif()
        {
            using (var scene = MediaFactory.Scene(3, 800, 600))
            using (var stored = (Bitmap)scene.Clone())
            {
                stored.RotateFlip(RotateFlipType.Rotate270FlipNone); // capteur : il faut tourner de 90° (orientation 6)
                var display = Print(scene);
                var data = MediaFactory.Encode(stored);
                Assert.True(display.Confirms(Decode(data, 6)));
                Assert.False(display.Confirms(Decode(data, 1)));
            }
        }

        // ---------- Copies WhatsApp « Sent » ----------

        /// <summary>Original de l'appareil photo (orientation 6, vignette EXIF) et sa copie WhatsApp recompressée.</summary>
        internal static (byte[] original, byte[] sent) PhotoPair(int seed, bool letterboxedThumb = false, int w = 1200, int h = 900)
        {
            using (var scene = MediaFactory.Scene(seed, w, h))
            using (var stored = (Bitmap)scene.Clone())
            {
                stored.RotateFlip(RotateFlipType.Rotate270FlipNone);
                byte[] thumb;
                if (letterboxedThumb)
                {
                    // Vignette 3:4 à bandes noires d'une photo 16:9 : cadrage différent.
                    using (var box = new Bitmap(120, 160))
                    using (var g = Graphics.FromImage(box))
                    {
                        g.Clear(Color.Black);
                        g.DrawImage(stored, 0, 20, 120, 120);
                        thumb = MediaFactory.Encode(box, 80);
                    }
                }
                else
                {
                    using (var small = MediaFactory.Resized(stored, stored.Width / 8, stored.Height / 8)) thumb = MediaFactory.Encode(small, 80);
                }
                var original = MediaFactory.CameraJpeg(stored, "2025:08:20 10:00:00", 6, thumb);
                using (var sent = MediaFactory.Resized(scene, w * 2 / 3, h * 2 / 3)) return (original, MediaFactory.Encode(sent, 60));
            }
        }

        [Fact]
        public void Copie_sent_retiree_quand_l_original_est_sur_la_cle()
        {
            var (original, sent) = PhotoPair(10);
            var (original2, sent2) = PhotoPair(11, letterboxedThumb: true, w: 1600, h: 900);
            var (_, sent3) = PhotoPair(12);         // original absent : gardée
            var (original4, sent4) = PhotoPair(13); // original daté APRÈS l'envoi : gardée
            var taken = new DateTime(2025, 8, 20, 10, 0, 0);
            var sentAt = new DateTime(2025, 8, 21, 18, 0, 0);
            Write("2025/08 - Août/20250820_100000.jpg", original, taken);
            Write("2025/08 - Août/20250820_100001.jpg", original2, taken);
            Write("2025/09 - Septembre/20250901_100000.jpg", original4, new DateTime(2025, 9, 1));
            Write("2025/08 - Août/IMG-20250821-WA0001.jpg", sent, sentAt);
            Write("2025/08 - Août/IMG-20250821-WA0002.jpg", sent2, sentAt);
            Write("2025/08 - Août/IMG-20250821-WA0003.jpg", sent3, sentAt);
            Write("2025/08 - Août/IMG-20250821-WA0004.jpg", sent4, sentAt);
            var phone = new List<PhoneFile>
            {
                Phone(SentFolder + "IMG-20250821-WA0001.jpg", sent.Length, sentAt, hidden: true),
                Phone(SentFolder + "IMG-20250821-WA0002.jpg", sent2.Length, sentAt, hidden: true),
                Phone(SentFolder + "IMG-20250821-WA0003.jpg", sent3.Length, sentAt, hidden: true),
                Phone(SentFolder + "IMG-20250821-WA0004.jpg", sent4.Length, sentAt, hidden: true),
            };
            var report = Run(phone);
            Assert.Equal(2, report.SentRemoved);
            string P(string name) => Path.Combine(Root, "2025", "08 - Août", name);
            Assert.False(File.Exists(P("IMG-20250821-WA0001.jpg")));
            Assert.False(File.Exists(P("IMG-20250821-WA0002.jpg")));
            Assert.Equal(sent3, File.ReadAllBytes(P("IMG-20250821-WA0003.jpg")));
            Assert.Equal(sent4, File.ReadAllBytes(P("IMG-20250821-WA0004.jpg")));
            Assert.Equal(original, File.ReadAllBytes(P("20250820_100000.jpg")));

            // Deuxième passage : plus rien.
            Assert.False(Run(phone).Changed);
        }

        [Fact]
        public void Copie_sent_gardee_si_un_element_visible_a_meme_nom_et_taille_ou_sans_dossier_sent()
        {
            var (original, sent) = PhotoPair(20);
            var at = new DateTime(2025, 8, 21);
            Write("a/20250820_100000.jpg", original, new DateTime(2025, 8, 20));
            Write("a/IMG-1.jpg", sent, at);
            Write("a/IMG-2.jpg", sent.Concat(new byte[] { 0 }).ToArray(), at);
            var phone = new List<PhoneFile>
            {
                Phone(SentFolder + "IMG-1.jpg", sent.Length, at, hidden: true),
                Phone("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-1.jpg", sent.Length, at),
                Phone("Pictures/Private/IMG-2.jpg", sent.Length + 1, at, hidden: true), // masqué, mais pas « Sent »
            };
            Assert.Equal(0, Run(phone).SentRemoved);
        }

        private static string Fixture(string name) => Path.Combine(Path.GetDirectoryName(new Uri(typeof(RepairV2Tests).Assembly.CodeBase).LocalPath), "Fixtures", name); // pas Location : copie fantôme de xunit

        [Fact]
        public void Images_d_une_vraie_video_decodees_par_media_foundation()
        {
            var fractions = new[] { 0.25, 0.5, 0.75 };
            var original = VideoFrames.Prints(Fixture("video-originale.mp4"), 3, 0, fractions);
            var copy = VideoFrames.Prints(Fixture("video-copie-whatsapp.mp4"), 3.018, 0, fractions);
            var other = VideoFrames.Prints(Fixture("video-autre.mp4"), 3, 0, fractions);
            Assert.NotNull(original);
            Assert.Equal(3, original.Count);
            Assert.True(SentCopies.SameVideo(copy, original));
            Assert.False(SentCopies.SameVideo(copy, other));
            Assert.Null(VideoFrames.Prints(Fixture("absente.mp4"), 3, 0, fractions));
        }

        [Fact]
        public void Video_sent_retiree_si_ses_images_sont_celles_de_l_original()
        {
            var at = new DateTime(2025, 8, 21, 18, 0, 0);
            void Copy(string fixture, string relative, DateTime date)
            {
                var path = Path.Combine(Root, relative.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.Copy(Fixture(fixture), path);
                File.SetLastWriteTime(path, date);
            }
            Copy("video-originale.mp4", "a/VID_1.mp4", new DateTime(2025, 8, 20));
            Copy("video-copie-whatsapp.mp4", "a/VID-WA1.mp4", at);
            // Même durée, même format, plus lourde, mais d'autres images : la copie reste.
            Copy("video-autre.mp4", "b/VID_2.mp4", new DateTime(2025, 8, 20));
            
            var size = new FileInfo(Fixture("video-copie-whatsapp.mp4")).Length;
            var phone = new List<PhoneFile> { Phone(SentVideos + "VID-WA1.mp4", size, at, hidden: true) };
            var report = Run(phone);
            Assert.Equal(1, report.SentRemoved);
            Assert.False(File.Exists(Path.Combine(Root, "a", "VID-WA1.mp4")));
            Assert.True(File.Exists(Path.Combine(Root, "a", "VID_1.mp4")));
        }

        [Fact]
        public void Video_sent_sans_original_de_memes_images_gardee()
        {
            var at = new DateTime(2025, 8, 21, 18, 0, 0);
            var copy = Path.Combine(Root, "x", "VID-WA1.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(copy));
            File.Copy(Fixture("video-copie-whatsapp.mp4"), copy);
            File.SetLastWriteTime(copy, at);
            File.Copy(Fixture("video-autre.mp4"), Path.Combine(Root, "x", "VID_2.mp4"));
            File.SetLastWriteTime(Path.Combine(Root, "x", "VID_2.mp4"), new DateTime(2025, 8, 20));
            var phone = new List<PhoneFile> { Phone(SentVideos + "VID-WA1.mp4", new FileInfo(copy).Length, at, hidden: true) };
            Assert.Equal(0, Run(phone).SentRemoved);
            Assert.True(File.Exists(copy));
        }

        [Fact]
        public void Video_sent_jamais_retiree_sur_la_seule_foi_des_metadonnees()
        {
            var at = new DateTime(2025, 8, 21, 18, 0, 0);
            Write("v/VID_1.mp4", MediaFactory.Movie(15.02, 1920, 1080, rotate90: true, audio: 15.0, mdatBytes: 50000), new DateTime(2025, 8, 20));
            Write("v/VID-WA1.mp4", MediaFactory.Movie(15.0, 848, 480, rotate90: true, audio: 14.98, mdatBytes: 5000), at);
            // Deux originaux possibles : on garde.
            Write("w/VID_2.mp4", MediaFactory.Movie(8, 1920, 1080, audio: 8, mdatBytes: 50000), new DateTime(2025, 8, 19));
            Write("w/VID_3.mp4", MediaFactory.Movie(8.05, 1280, 720, audio: 8.05, mdatBytes: 40000), new DateTime(2025, 8, 18));
            Write("w/VID-WA2.mp4", MediaFactory.Movie(8.02, 640, 360, audio: 8.02, mdatBytes: 5000), at);
            // Original trop ancien (plus de 30 jours) ; autre format d'image ; sans son.
            Write("x/VID_4.mp4", MediaFactory.Movie(20, 1920, 1080, audio: 20, mdatBytes: 50000), new DateTime(2025, 6, 1));
            Write("x/VID-WA3.mp4", MediaFactory.Movie(20, 640, 360, audio: 20, mdatBytes: 5000), at);
            Write("x/VID_5.mp4", MediaFactory.Movie(30, 1920, 1080, audio: 30, mdatBytes: 50000), new DateTime(2025, 8, 20));
            Write("x/VID-WA4.mp4", MediaFactory.Movie(30, 480, 480, audio: 30, mdatBytes: 5000), at);
            Write("x/VID_6.mp4", MediaFactory.Movie(40, 1920, 1080, audio: 40, mdatBytes: 50000), new DateTime(2025, 8, 20));
            Write("x/VID-WA5.mp4", MediaFactory.Movie(40, 640, 360, mdatBytes: 5000), at);
            var phone = new[] { "VID-WA1.mp4", "VID-WA2.mp4", "VID-WA3.mp4", "VID-WA4.mp4", "VID-WA5.mp4" }
                .Select(n => Phone(SentVideos + n, new FileInfo(Directory.GetFiles(Root, n, SearchOption.AllDirectories).Single()).Length, at, hidden: true))
                .ToList();
            var report = Run(phone);
            // Vidéos sans images décodables : durée, format et dates ne suffisent jamais (de vrais clips
            // différents ont la même durée), tout est gardé.
            Assert.Equal(0, report.SentRemoved);
            Assert.Equal(5, Directory.GetFiles(Root, "VID-WA*.mp4", SearchOption.AllDirectories).Length);
        }

        [Fact]
        public void Photos_examinees_retenues_d_une_analyse_a_l_autre()
        {
            var date = new DateTime(2025, 3, 1, 12, 0, 0);
            var still = MediaFactory.JpegWithExif("2025:03:01 12:00:00");
            var video = MediaFactory.Mp4();
            var motion = MediaFactory.WithSef(still, ("MotionPhoto_Data", 0x0A30, video));
            var plain = MediaFactory.WithSef(still, ("Image_UTC_Data", 0x0A01, new byte[video.Length + 2])); // même taille
            Assert.Equal(motion.Length, plain.Length);
            var path = Write("2025/a.jpg", plain, date);
            Assert.False(Run().Changed);
            Assert.True(File.GetAttributes(Path.Combine(Root, SeenPhotos.FileName)).HasFlag(FileAttributes.Hidden));

            // Même taille, même date : la photo n'est pas rouverte (preuve : un contenu changé en douce reste tel quel).
            File.WriteAllBytes(path, motion);
            File.SetLastWriteTime(path, date);
            Assert.Equal(0, Run().MotionStripped);
            // Date changée : relue, raccourcie.
            File.SetLastWriteTime(path, date.AddMinutes(1));
            Assert.Equal(1, Run().MotionStripped);
            Assert.Equal(still, File.ReadAllBytes(path));
        }

        [Fact]
        public void Doublon_retire_transmet_son_entree_d_index()
        {
            var still = MediaFactory.Jpeg(30, 30);
            var kept = Write("x/A.jpg", still);
            var extra = Write("x/A (2).jpg", still);
            CopyIndex.Append(Root, "A.jpg", 9999, extra, still.Length);
            var report = Run();
            Assert.Equal(1, report.Duplicates);
            Assert.True(File.Exists(kept));
            var index = DuplicateIndex.ForTarget(Target.ForDrive(new DriveDto { Id = "T", Root = drive }, null));
            Assert.True(index.IsOnDrive("A.jpg", 9999, () => null, _ => null));
        }
    }
}
