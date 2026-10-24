using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using YayoPont.Core;
using YayoPont.Drives;
using YayoPont.Jobs;
using YayoPont.Phone;
using Xunit;

namespace YayoPont.Tests
{
    public sealed class JobsV2Tests : IDisposable
    {
        private readonly string phoneDir = Path.Combine(Path.GetTempPath(), "yayo-tel2-" + Guid.NewGuid().ToString("N"));
        private readonly string driveDir = Path.Combine(Path.GetTempPath(), "yayo-cle2-" + Guid.NewGuid().ToString("N"));
        private readonly byte[] still = MediaFactory.JpegWithExif("2025:07:14 10:00:00", 45.764, 4.8357);
        private readonly byte[] motion;

        public JobsV2Tests()
        {
            motion = MediaFactory.SamsungMotion(still, MediaFactory.Mp4(2, mdatBytes: 30000));
            WritePhone("DCIM/Camera/20250714_100000.jpg", motion);
            WritePhone("DCIM/Camera/IMG_5.HEIC", MediaFactory.Heic("2024:12:25 18:00:00", 48.8584, 2.2945));
            WritePhone("DCIM/Camera/IMG_5.MOV", MediaFactory.Mp4(2));                         // clip de photo animée
            WritePhone("DCIM/Camera/VID_1.mp4", MediaFactory.Mp4(20, "+43.2965+005.3698/", 5000));
            WritePhone("WhatsApp/Media/WhatsApp Images/IMG-1.jpg", MediaFactory.Jpeg(30, 30));
            WritePhone("WhatsApp/Media/WhatsApp Images/Sent/IMG-2.jpg", MediaFactory.Jpeg(31, 30));
            WritePhone("Download/IMG-1 (1).jpg", MediaFactory.Jpeg(30, 30));                  // doublon interne
            WritePhone("Pictures/Private/.nomedia", new byte[0]);
            WritePhone("Pictures/Private/secret.jpg", MediaFactory.Jpeg(32, 30));
            Directory.CreateDirectory(driveDir);
        }

        public void Dispose()
        {
            Directory.Delete(phoneDir, true);
            foreach (var file in Directory.GetFiles(driveDir, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(driveDir, true);
        }

        private void WritePhone(string relative, byte[] data)
        {
            var path = Path.Combine(phoneDir, relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        private Target Drive(string rootName = null) => Target.ForDrive(new FakeDriveProvider(driveDir).List().Single(), rootName);

        private (FolderPhoneSource phone, ScanJob scan) Scan(bool deep = false, Target target = null)
        {
            var phone = new FolderPhoneSource(phoneDir);
            var scan = new ScanJob();
            scan.Run(phone, null, target ?? Drive(), new ScanOptions { Deep = deep }, CancellationToken.None);
            Assert.Equal("done", scan.State);
            return (phone, scan);
        }

        private static MediaItem Item(ScanJob scan, string name) => scan.Items.Single(i => i.Name == name);

        [Fact]
        public void Analyse_marque_masques_clips_et_doublons()
        {
            var (_, scan) = Scan();
            Assert.Equal(8, scan.Items.Count);
            Assert.Equal(2, scan.Hidden);
            Assert.Equal(1, scan.Live);
            Assert.Equal(1, scan.Duplicates);
            Assert.True(Item(scan, "IMG-2.jpg").Hidden);
            Assert.True(Item(scan, "secret.jpg").Hidden);
            Assert.True(Item(scan, "IMG_5.MOV").Live);
            Assert.False(Item(scan, "VID_1.mp4").Live);
            Assert.Equal(Item(scan, "IMG-1.jpg").Id, Item(scan, "IMG-1 (1).jpg").DuplicateOf);
            Assert.Null(Item(scan, "IMG-1.jpg").DuplicateOf);
            Assert.Equal("DCIM/Camera/VID_1.mp4", Item(scan, "VID_1.mp4").Path);
            Assert.Null(Item(scan, "20250714_100000.jpg").ExifDate); // pas d'analyse approfondie
            Assert.Equal(0, scan.DetailsTotal);
        }

        [Fact]
        public void Analyse_approfondie_lit_exif_gps_et_drapeaux()
        {
            WritePhone("DCIM/Camera/PXL_1.MP.jpg", MediaFactory.GoogleMotion(MediaFactory.Mp4()));
            var (_, scan) = Scan(deep: true);
            Assert.Equal("details", scan.Phase); // dernière phase atteinte
            Assert.Equal(9, scan.DetailsTotal); // 6 jpg, 1 heic, 1 mov, 1 mp4
            Assert.Equal(scan.DetailsTotal, scan.DetailsDone);
            var photo = Item(scan, "20250714_100000.jpg");
            Assert.Equal(new DateTime(2025, 7, 14, 10, 0, 0), photo.ExifDate);
            Assert.Equal(45.764, photo.Lat.Value, 4);
            var heic = Item(scan, "IMG_5.HEIC");
            Assert.Equal(2024, heic.ExifDate.Value.Year);
            Assert.Equal(2.2945, heic.Lon.Value, 4);
            Assert.Equal(5.3698, Item(scan, "VID_1.mp4").Lon.Value, 4);
            Assert.True(Item(scan, "PXL_1.MP.jpg").Motion);
            Assert.Null(photo.Motion); // Samsung : connu seulement en lisant tout le fichier
        }

        [Fact]
        public void Copie_retire_la_video_et_ne_recopie_pas_ensuite()
        {
            var (phone, scan) = Scan();
            var visible = scan.Files.Values.Where(f => !f.Hidden && !f.Live && f.DuplicateOf == null).ToList();
            var job = new TransferJob();
            job.Run(phone, scan.Target, visible, scan.Index);
            Assert.Equal("done", job.State);
            Assert.Empty(job.Failed);
            Assert.Equal(4, job.Copied);
            Assert.Equal("copy", job.Phase);
            Assert.Equal(4, job.CopiedFiles.Count);

            var photo = Path.Combine(driveDir, "Photos du téléphone", "2025", "07 - Juillet", "20250714_100000.jpg");
            Assert.Equal(still, File.ReadAllBytes(photo));
            Assert.Equal(new DateTime(2025, 7, 14, 10, 0, 0), File.GetLastWriteTime(photo));
            Assert.Contains(job.CopiedFiles, c => c.Path == "Photos du téléphone/2025/07 - Juillet/20250714_100000.jpg");
            Assert.True(File.Exists(Path.Combine(driveDir, "Photos du téléphone", "2024", "12 - Décembre", "IMG_5.HEIC")));

            // La photo raccourcie est reconnue grâce à l'index des copies.
            var (_, again) = Scan();
            Assert.True(Item(again, "20250714_100000.jpg").OnDrive);
            Assert.All(visible, f => Assert.True(again.Files[f.Id].OnDrive));
        }

        [Fact]
        public void La_copie_range_d_abord_la_cle()
        {
            var old = Path.Combine(driveDir, "Photos du téléphone", "2025", "07 - Juillet");
            Directory.CreateDirectory(old);
            File.WriteAllBytes(Path.Combine(old, "20250714_100000.jpg"), motion);     // copié par le pont 1.0.1
            File.WriteAllBytes(Path.Combine(old, "20250714_100000 (2).jpg"), motion); // doublon
            File.WriteAllBytes(Path.Combine(old, "IMG_5.MOV"), MediaFactory.Mp4(2));
            File.WriteAllBytes(Path.Combine(old, "IMG_5.HEIC"), new byte[10]);
            var (phone, scan) = Scan();
            var job = new TransferJob();
            job.Run(phone, scan.Target, scan.Files.Values.Where(f => f.Name == "20250714_100000.jpg").ToList(), scan.Index);
            Assert.Equal("done", job.State);
            Assert.Equal(1, job.Repair.Duplicates);
            Assert.Equal(1, job.Repair.MotionStripped);
            Assert.Equal(1, job.Repair.LiveRemoved);
            Assert.Equal(0, job.Copied);
            Assert.Equal(1, job.Skipped); // la photo raccourcie sur place compte comme déjà copiée
            Assert.Equal(still, File.ReadAllBytes(Path.Combine(old, "20250714_100000.jpg")));
        }

        [Fact]
        public void Video_incomplete_echoue_sans_rester_sur_la_cle()
        {
            var full = MediaFactory.Mp4(20, mdatBytes: 5000);
            WritePhone("DCIM/Camera/VID_1.mp4", full.Take(full.Length - 100).ToArray());
            var (phone, scan) = Scan();
            var job = new TransferJob();
            job.Run(phone, scan.Target, scan.Files.Values.Where(f => f.Name == "VID_1.mp4").ToList(), scan.Index);
            Assert.Equal("done", job.State);
            Assert.Equal(0, job.Copied);
            Assert.Contains("Vidéo incomplète", job.Failed.Single().Error);
            Assert.Empty(Directory.GetFiles(driveDir, "*.mp4*", SearchOption.AllDirectories));
        }

        [Fact]
        public void Options_avancees_modele_nom_lieux_conflits_verification()
        {
            var folder = Path.Combine(Path.GetTempPath(), "yayo-dossier-" + Guid.NewGuid().ToString("N"));
            try
            {
                var target = Target.ForFolder(folder, "", out var error);
                Assert.Null(error);
                var (phone, scan) = Scan(target: target);
                var heic = scan.Files.Values.Single(f => f.Name == "IMG_5.HEIC");
                var options = new TransferOptions
                {
                    Layout = "{pays}/{annee}",
                    FileName = "{date} {nom}",
                    Places = new Dictionary<string, object> { [heic.Id] = new Dictionary<string, object> { ["pays"] = "France" } },
                    Verify = true,
                    KeepDates = false,
                    StripMotion = false,
                };
                var job = new TransferJob();
                job.Run(phone, target, new List<PhoneFile> { heic, scan.Files.Values.Single(f => f.Name == "20250714_100000.jpg") }, scan.Index, options);
                Assert.Equal("done", job.State);
                Assert.Empty(job.Failed);
                var written = Path.Combine(folder, "France", "2024", "2024-12-25 IMG_5.HEIC");
                Assert.True(File.Exists(written));
                Assert.NotEqual(2024, File.GetLastWriteTime(written).Year);
                Assert.Equal(motion, File.ReadAllBytes(Path.Combine(folder, "Pays inconnu", "2025", "2025-07-14 20250714_100000.jpg")));
                Assert.Equal("France/2024/2024-12-25 IMG_5.HEIC", job.CopiedFiles.Single(c => c.Id == heic.Id).Path);

                // Conflits : même nom, contenu différent.
                File.WriteAllBytes(written, new byte[] { 1, 2, 3 });
                var skip = new TransferJob();
                skip.Run(phone, target, new List<PhoneFile> { heic }, null, new TransferOptions { Layout = options.Layout, FileName = options.FileName, Places = options.Places, Conflict = "skip" });
                Assert.Equal(1, skip.Skipped);
                Assert.Equal(3, new FileInfo(written).Length);
                var overwrite = new TransferJob();
                overwrite.Run(phone, target, new List<PhoneFile> { heic }, null, new TransferOptions { Layout = options.Layout, FileName = options.FileName, Places = options.Places, Conflict = "overwrite" });
                Assert.Equal(1, overwrite.Copied);
                Assert.Equal(heic.Size, new FileInfo(written).Length);
                Assert.Single(Directory.GetFiles(Path.GetDirectoryName(written)));
            }
            finally
            {
                if (Directory.Exists(folder))
                {
                    foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(folder, true);
                }
            }
        }

        [Fact]
        public void Sans_dedoublonnage_on_recopie()
        {
            var (phone, scan) = Scan();
            var one = scan.Files.Values.Where(f => f.Name == "IMG-1.jpg").ToList();
            new TransferJob().Run(phone, scan.Target, one, scan.Index);
            var again = new TransferJob();
            again.Run(phone, scan.Target, one, null, new TransferOptions { Dedupe = false, Repair = false });
            Assert.Equal(1, again.Copied);
            Assert.Equal(2, Directory.GetFiles(driveDir, "IMG-1*.jpg", SearchOption.AllDirectories).Length);
        }

        [Fact]
        public void Apercu_du_rangement_sans_rien_ecrire()
        {
            var (_, scan) = Scan();
            var files = scan.Files.Values.ToList();
            files.Single(f => f.Name == "IMG-1.jpg").OnDrive = true;
            var plan = TransferPlan.Build(scan.Target, files, new TransferOptions { Layout = "{type}" });
            Assert.Equal(7, plan["toCopy"]);
            Assert.Equal(1, plan["skipped"]);
            var folders = ((IEnumerable<Dictionary<string, object>>)plan["folders"]).ToList();
            Assert.Equal(new[] { "Photos du téléphone/Photos", "Photos du téléphone/Vidéos" }, folders.Select(f => (string)f["path"]).ToArray());
            Assert.Equal(2, folders[1]["count"]);
            Assert.Equal(files.Where(f => !f.OnDrive).Sum(f => f.Size), plan["bytes"]);
            Assert.Empty(Directory.GetFileSystemEntries(driveDir));
        }
    }
}
