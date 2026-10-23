using System;
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
    public sealed class JobsTests : IDisposable
    {
        private readonly string phoneDir = Path.Combine(Path.GetTempPath(), "yayo-tel-" + Guid.NewGuid().ToString("N"));
        private readonly string driveDir = Path.Combine(Path.GetTempPath(), "yayo-cle-" + Guid.NewGuid().ToString("N"));

        public JobsTests()
        {
            WritePhone("DCIM/Camera/IMG_1.jpg", ExifAndDestinationTests.JpegWithExif("2025:07:14 10:00:00", false).Concat(new byte[5000]).ToArray());
            WritePhone("DCIM/Camera/IMG_2.jpg", Enumerable.Repeat((byte)7, 3000).ToArray());
            WritePhone("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/VID-1.mp4", Enumerable.Repeat((byte)9, 8000).ToArray());
            File.SetLastWriteTime(Path.Combine(phoneDir, "DCIM", "Camera", "IMG_2.jpg"), new DateTime(2026, 10, 1, 9, 0, 0));
            File.SetLastWriteTime(Path.Combine(phoneDir, "Android", "media", "com.whatsapp", "WhatsApp", "Media", "WhatsApp Video", "VID-1.mp4"), new DateTime(2026, 10, 2, 9, 0, 0));
            Directory.CreateDirectory(driveDir);
            // Déjà sur la clé, sous un autre nom : doit être reconnu par son contenu.
            Directory.CreateDirectory(Path.Combine(driveDir, "Vieux"));
            File.WriteAllBytes(Path.Combine(driveDir, "Vieux", "copie.jpg"), Enumerable.Repeat((byte)7, 3000).ToArray());
        }

        public void Dispose()
        {
            Directory.Delete(phoneDir, true);
            Directory.Delete(driveDir, true);
        }

        private void WritePhone(string relative, byte[] data)
        {
            var path = Path.Combine(phoneDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        private (FolderPhoneSource phone, DriveDto drive) Setup() =>
            (new FolderPhoneSource(phoneDir), new FakeDriveProvider(driveDir).List().Single());

        [Fact]
        public void Analyse_compte_et_reconnait_les_copies_renommees()
        {
            var (phone, drive) = Setup();
            var scan = new ScanJob();
            scan.Run(phone, drive, CancellationToken.None);
            Assert.Equal("done", scan.State);
            Assert.Equal(2, scan.Photos);
            Assert.Equal(1, scan.Videos);
            Assert.Equal(1, scan.OnDriveCount);
            Assert.True(scan.Items.Single(i => i.Name == "IMG_2.jpg").OnDrive);
        }

        [Fact]
        public void Transfert_range_par_date_exif_et_saute_les_doublons()
        {
            var (phone, drive) = Setup();
            var scan = new ScanJob();
            scan.Run(phone, drive, CancellationToken.None);
            var job = new TransferJob();
            job.Run(phone, drive, scan.Files.Values.ToList(), scan.Index);

            Assert.Equal("done", job.State);
            Assert.Equal(2, job.Copied);
            Assert.Equal(1, job.Skipped);
            Assert.Empty(job.Failed);
            Assert.Equal(job.BytesTotal, job.BytesDone);

            var img1 = Path.Combine(driveDir, "Photos du téléphone", "2025", "07 - Juillet", "IMG_1.jpg");
            var vid = Path.Combine(driveDir, "Photos du téléphone", "2026", "10 - Octobre", "VID-1.mp4");
            Assert.True(File.Exists(img1));
            Assert.True(File.Exists(vid));
            Assert.Equal(new DateTime(2025, 7, 14, 10, 0, 0), File.GetLastWriteTime(img1));
            Assert.Empty(Directory.GetFiles(driveDir, "*.part", SearchOption.AllDirectories));

            // Deuxième passage : tout est déjà sur la clé.
            var again = new ScanJob();
            again.Run(phone, drive, CancellationToken.None);
            Assert.True(again.Items.All(i => i.OnDrive));
        }

        [Fact]
        public void Meme_nom_contenu_different_ne_s_ecrase_pas()
        {
            var (phone, drive) = Setup();
            var folder = Path.Combine(driveDir, "Photos du téléphone", "2026", "10 - Octobre");
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "VID-1.mp4"), new byte[10]); // autre fichier, autre taille
            var scan = new ScanJob();
            scan.Run(phone, drive, CancellationToken.None);
            var job = new TransferJob();
            job.Run(phone, drive, scan.Files.Values.Where(f => f.Name == "VID-1.mp4").ToList(), scan.Index);
            Assert.Equal(1, job.Copied);
            Assert.Equal(10, new FileInfo(Path.Combine(folder, "VID-1.mp4")).Length);
            Assert.Equal(8000, new FileInfo(Path.Combine(folder, "VID-1 (2).mp4")).Length);
        }

        [Fact]
        public void Annulation_ne_laisse_pas_de_fichier_partiel()
        {
            var (phone, drive) = Setup();
            var scan = new ScanJob();
            scan.Run(phone, drive, CancellationToken.None);
            var job = new TransferJob();
            job.Cancel();
            job.Run(phone, drive, scan.Files.Values.ToList(), scan.Index);
            Assert.Equal("cancelled", job.State);
            Assert.Equal(0, job.Copied);
            Assert.Empty(Directory.GetFiles(driveDir, "*.part", SearchOption.AllDirectories));
        }
    }
}
