using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public sealed class KeyRepairTests : IDisposable
    {
        private readonly string drive = Path.Combine(Path.GetTempPath(), "yayo-range-" + Guid.NewGuid().ToString("N"));
        private string Root => Path.Combine(drive, "Photos du téléphone");

        public void Dispose()
        {
            foreach (var file in Directory.GetFiles(drive, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(drive, true);
        }

        private string Write(string relative, byte[] data, string under = null)
        {
            var path = Path.Combine(under ?? Root, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            return path;
        }

        private static RepairReport Run(string root, ISet<string> names = null, bool stripMotion = true, bool removeLive = true) =>
            KeyRepair.Run(root, stripMotion, removeLive, names ?? new HashSet<string>(), CancellationToken.None);

        [Fact]
        public void Index_des_copies_cache_et_seulement_les_entrees_valides()
        {
            var photo = Write("2026/a.jpg", new byte[10]);
            CopyIndex.Append(Root, "IMG_1.jpg", 5000, photo, 10);
            CopyIndex.Append(Root, "IMG_2.jpg", 6000, Path.Combine(Root, "2026", "absent.jpg"), 10);
            CopyIndex.Append(Root, "IMG_3.jpg", 7000, photo + "x", 99);
            CopyIndex.Append(Root, "hors.jpg", 1, Path.Combine(drive, "hors.jpg"), 1); // hors du dossier géré : ignoré
            var attributes = File.GetAttributes(CopyIndex.PathFor(Root));
            Assert.True(attributes.HasFlag(FileAttributes.Hidden) && attributes.HasFlag(FileAttributes.System));
            CopyIndex.Append(Root, "IMG_4.jpg", 8000, photo, 10); // fichier caché : l'ajout marche encore
            var entries = CopyIndex.Load(Root);
            // Toutes les entrées valides d'un chemin comptent (un doublon retiré lègue les siennes).
            Assert.Equal(new[] { "IMG_1.jpg", "IMG_4.jpg" }, entries.Select(e => e.PhoneName).OrderBy(n => n).ToArray());
            Assert.Equal(8000, entries.Single(e => e.PhoneName == "IMG_4.jpg").PhoneSize);
        }

        [Fact]
        public void Index_des_doublons_reconnait_une_photo_animee_raccourcie()
        {
            var photo = Write("2026/IMG_1.jpg", new byte[10]);
            CopyIndex.Append(Root, "IMG_1.jpg", 5000, photo, 10);
            var index = DuplicateIndex.ForTarget(Target.ForDrive(new DriveDto { Id = "T", Root = drive }, null));
            Assert.True(index.IsOnDrive("img_1.JPG", 5000, () => null, _ => null));
            Assert.False(index.IsOnDrive("IMG_1.jpg", 5001, () => null, _ => null));
        }

        [Fact]
        public void Video_incomplete_absente_de_l_index_des_doublons()
        {
            var full = MediaFactory.Mp4();
            Write("ok.mp4", full);
            Write("casse.mp4", full.Take(full.Length - 50).ToArray());
            Write("autre.mp4", new byte[300]); // pas un MP4 reconnaissable : gardé
            var names = DuplicateIndex.MediaFiles(Root).Select(f => Path.GetFileName(f.path)).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "autre.mp4", "ok.mp4" }, names);
        }

        [Fact]
        public void Range_la_cle_en_six_etapes()
        {
            var jpeg = MediaFactory.Jpeg(120, 90);
            var still = MediaFactory.Jpeg(121, 90);
            var samsung = MediaFactory.SamsungMotion(still, MediaFactory.Mp4(3, mdatBytes: 20000));
            var full = MediaFactory.Mp4(10, mdatBytes: 5000);
            Write("2026/10 - Octobre/IMG_1.jpg.part", new byte[100]);         // 1. reste de copie
            Write("2026/10 - Octobre/notes.part", new byte[7]);                // pas un média : gardé
            Write("2026/10 - Octobre/A.jpg", jpeg);                            // 2. doublons exacts
            Write("2026/10 - Octobre/A (2).jpg", jpeg);
            Write("2025/A.jpg", jpeg);
            Write("2026/10 - Octobre/20261024_1.jpg", samsung);                // 3. photo animée
            Write("2026/10 - Octobre/IMG_9.HEIC", new byte[500]);             // 4. clip de photo animée
            Write("2026/10 - Octobre/IMG_9.MOV", MediaFactory.Mp4(2));
            Write("2026/10 - Octobre/IMG_8.jpg", MediaFactory.Jpeg(10, 10));  //    vraie vidéo de même nom : gardée
            Write("2026/10 - Octobre/IMG_8.mp4", MediaFactory.Mp4(30));
            Write("2026/09 - Septembre/VID_1.mp4", full.Take(full.Length - 100).ToArray()); // 5. vidéo incomplète recopiée
            Write("2026/09 - Septembre/VID_2.mp4", full.Take(full.Length - 99).ToArray());  //    pas dans la copie : gardée
            Directory.CreateDirectory(Path.Combine(Root, "2024", "vide"));                  // 6. dossier vide
            var outside = Write("Vacances/A.jpg", jpeg, drive);                             // hors du dossier géré

            var report = Run(Root, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "vid_1.mp4" });

            Assert.Equal(1, report.PartsRemoved);
            Assert.Equal(2, report.Duplicates);
            Assert.Equal(1, report.MotionStripped);
            Assert.Equal(1, report.LiveRemoved);
            Assert.Equal(1, report.BrokenRemoved);
            Assert.True(report.BytesFreed > 20000);

            string P(string r) => Path.Combine(Root, r.Replace('/', '\\'));
            Assert.False(File.Exists(P("2026/10 - Octobre/IMG_1.jpg.part")));
            Assert.True(File.Exists(P("2026/10 - Octobre/notes.part")));
            Assert.True(File.Exists(P("2025/A.jpg")));                          // nom sans (n), chemin le plus court
            Assert.False(File.Exists(P("2026/10 - Octobre/A.jpg")));
            Assert.False(File.Exists(P("2026/10 - Octobre/A (2).jpg")));
            Assert.Equal(still, File.ReadAllBytes(P("2026/10 - Octobre/20261024_1.jpg")));
            Assert.False(File.Exists(P("2026/10 - Octobre/IMG_9.MOV")));
            Assert.True(File.Exists(P("2026/10 - Octobre/IMG_8.mp4")));
            Assert.False(File.Exists(P("2026/09 - Septembre/VID_1.mp4")));
            Assert.True(File.Exists(P("2026/09 - Septembre/VID_2.mp4")));
            Assert.False(Directory.Exists(P("2024")));
            Assert.True(File.Exists(outside));

            var entry = CopyIndex.Load(Root).Single();
            Assert.Equal("20261024_1.jpg", entry.PhoneName);
            Assert.Equal(samsung.Length, entry.PhoneSize);
            Assert.Equal(still.Length, entry.WrittenSize);

            // Deuxième passage : plus rien à faire.
            var again = Run(Root);
            Assert.False(again.Changed);
        }

        [Fact]
        public void Sans_retrait_des_photos_animees_rien_n_est_raccourci_ni_supprime()
        {
            var samsung = MediaFactory.SamsungMotion(MediaFactory.Jpeg(), MediaFactory.Mp4());
            Write("x/P.jpg", samsung);
            Write("x/P.mp4", MediaFactory.Mp4(2));
            var report = Run(Root, stripMotion: false);
            Assert.Equal(0, report.MotionStripped + report.LiveRemoved);
            Assert.Equal(samsung, File.ReadAllBytes(Path.Combine(Root, "x", "P.jpg")));
        }

        [Fact]
        public void Raccourcissement_interrompu_remis_en_place()
        {
            var jpeg = MediaFactory.Jpeg();
            Write("x/P.jpg" + KeyRepair.RepairSuffix, jpeg);
            Write("x/Q.jpg" + KeyRepair.RepairSuffix, jpeg);
            Write("x/Q.jpg", jpeg);
            Run(Root);
            Assert.Equal(jpeg, File.ReadAllBytes(Path.Combine(Root, "x", "P.jpg")));
            Assert.False(File.Exists(Path.Combine(Root, "x", "Q.jpg" + KeyRepair.RepairSuffix)));
        }

        [Fact]
        public void Racine_qui_est_toute_la_cle_seuls_les_dossiers_touches_sont_retires()
        {
            Directory.CreateDirectory(Path.Combine(drive, "Dossier de Yayo"));
            Write("a/b/x.jpg.part", new byte[3], drive);
            var report = KeyRepair.Run(drive, true, true, new HashSet<string>(), CancellationToken.None, ownRoot: false);
            Assert.Equal(1, report.PartsRemoved);
            Assert.True(Directory.Exists(Path.Combine(drive, "Dossier de Yayo")));
            Assert.False(Directory.Exists(Path.Combine(drive, "a")));
        }
    }
}
