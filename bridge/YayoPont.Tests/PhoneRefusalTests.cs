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
    /// <summary>
    /// Un téléphone qui liste ses photos mais refuse d'en livrer le contenu (MTP bloqué, 0x80070141 sur un S23) :
    /// le pont doit le dire tout de suite et s'arrêter, pas enchaîner 800 échecs ni annoncer « rien de nouveau ».
    /// </summary>
    public sealed class PhoneRefusalTests : IDisposable
    {
        private const int Refusal = unchecked((int)0x80070141);

        private readonly string phoneDir = Path.Combine(Path.GetTempPath(), "yayo-refus-tel-" + Guid.NewGuid().ToString("N"));
        private readonly string driveDir = Path.Combine(Path.GetTempPath(), "yayo-refus-cle-" + Guid.NewGuid().ToString("N"));

        public PhoneRefusalTests()
        {
            for (int i = 0; i < 12; i++)
                Write($"DCIM/Camera/IMG_{i}.jpg", Enumerable.Repeat((byte)i, 2000 + i).ToArray());
            for (int i = 0; i < 12; i++)
                Write($"Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/IMG-2026110{i % 9}-WA{i:0000}.jpg", Enumerable.Repeat((byte)(50 + i), 3000 + i).ToArray());
            Directory.CreateDirectory(driveDir);
        }

        public void Dispose()
        {
            Directory.Delete(phoneDir, true);
            Directory.Delete(driveDir, true);
        }

        private void Write(string relative, byte[] data)
        {
            var path = Path.Combine(phoneDir, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        private Target Drive() => Target.ForDrive(new FakeDriveProvider(driveDir).List().Single(), null);

        /// <summary>Refuse la lecture des fichiers choisis, comme le fait le téléphone.</summary>
        private sealed class RefusingPhone : IPhoneSource
        {
            private readonly IPhoneSource inner;
            private readonly Func<string, bool> refuses;
            public int Opened;

            public RefusingPhone(IPhoneSource inner, Func<string, bool> refuses)
            {
                this.inner = inner;
                this.refuses = refuses;
            }

            public IList<PhoneInfo> Phones() => inner.Phones();
            public IList<PhoneFile> Enumerate(string phoneId, Action<PhoneFile> found, CancellationToken cancel) => inner.Enumerate(phoneId, found, cancel);
            public byte[] Thumbnail(string id) => inner.Thumbnail(id);
            public bool UsbSeen() => inner.UsbSeen();
            public void Dispose() => inner.Dispose();

            public Stream OpenRead(string id)
            {
                Opened++;
                if (refuses(id)) throw PhoneReadException.Refused(Refusal);
                return inner.OpenRead(id);
            }
        }

        [Fact]
        public void L_analyse_signale_un_telephone_qui_refuse_tout()
        {
            using (var phone = new RefusingPhone(new FolderPhoneSource(phoneDir), _ => true))
            {
                var scan = new ScanJob();
                scan.Run(phone, Drive(), CancellationToken.None);

                Assert.Equal("done", scan.State);
                Assert.True(scan.ReadBlocked);
                Assert.Contains("0x80070141", scan.ReadError);
                Assert.All(scan.Items, i => Assert.True(i.Unreadable));
            }
        }

        [Fact]
        public void L_analyse_ne_marque_que_la_provenance_refusee()
        {
            using (var phone = new RefusingPhone(new FolderPhoneSource(phoneDir), id => id.Contains("WhatsApp")))
            {
                var scan = new ScanJob();
                scan.Run(phone, Drive(), CancellationToken.None);

                Assert.False(scan.ReadBlocked);
                Assert.Equal(new[] { "whatsapp" }, scan.BlockedSources.ToArray());
                Assert.All(scan.Items.Where(i => i.Source == "whatsapp"), i => Assert.True(i.Unreadable));
                Assert.All(scan.Items.Where(i => i.Source == "camera"), i => Assert.False(i.Unreadable));
            }
        }

        [Fact]
        public void Un_telephone_qui_lit_normalement_n_est_pas_marque()
        {
            using (var phone = new FolderPhoneSource(phoneDir))
            {
                var scan = new ScanJob();
                scan.Run(phone, Drive(), CancellationToken.None);

                Assert.False(scan.ReadBlocked);
                Assert.Empty(scan.BlockedSources);
                Assert.Null(scan.ReadError);
                Assert.All(scan.Items, i => Assert.False(i.Unreadable));
            }
        }

        [Fact]
        public void La_copie_s_arrete_au_cinquieme_refus_sans_rien_copier()
        {
            using (var phone = new RefusingPhone(new FolderPhoneSource(phoneDir), _ => true))
            {
                var scan = new ScanJob();
                scan.Run(new FolderPhoneSource(phoneDir), Drive(), CancellationToken.None);
                var files = scan.Files.Values.ToList();
                var job = new TransferJob();
                job.Run(phone, Drive(), files, scan.Index);

                Assert.Equal("error", job.State);
                Assert.Contains("0x80070141", job.Error);
                Assert.Equal(0, job.Copied);
                Assert.Equal(5, job.Failed.Count);
                Assert.Equal(5, phone.Opened);
            }
        }

        /// <summary>Fichiers vus à l'inventaire que le téléphone ne connaît plus (temporaires de l'A14).</summary>
        private sealed class GonePhone : IPhoneSource
        {
            private readonly IPhoneSource inner;
            private readonly Func<string, bool> gone;

            public GonePhone(IPhoneSource inner, Func<string, bool> gone)
            {
                this.inner = inner;
                this.gone = gone;
            }

            public IList<PhoneInfo> Phones() => inner.Phones();
            public IList<PhoneFile> Enumerate(string phoneId, Action<PhoneFile> found, CancellationToken cancel) => inner.Enumerate(phoneId, found, cancel);
            public byte[] Thumbnail(string id) => inner.Thumbnail(id);
            public bool UsbSeen() => inner.UsbSeen();
            public void Dispose() => inner.Dispose();
            public Stream OpenRead(string id) => gone(id) ? throw new PhoneFileGoneException() : inner.OpenRead(id);
        }

        [Fact]
        public void Des_fichiers_disparus_sont_passes_sans_arreter_ni_marquer_le_telephone()
        {
            // Dix d'affilée au début : ce ne sont pas des refus du téléphone, la copie continue.
            using (var phone = new GonePhone(new FolderPhoneSource(phoneDir), id => id.StartsWith("Android") && !id.EndsWith("0000.jpg") && !id.EndsWith("0011.jpg")))
            {
                var scan = new ScanJob();
                scan.Run(phone, Drive(), CancellationToken.None);
                Assert.False(scan.ReadBlocked);
                Assert.Empty(scan.BlockedSources);

                var job = new TransferJob();
                job.Run(phone, Drive(), scan.Files.Values.Where(f => f.Source == "whatsapp").ToList(), scan.Index);
                Assert.Equal("done", job.State);
                Assert.Equal(2, job.Copied);
                Assert.Equal(10, job.Failed.Count);
                Assert.All(job.Failed, f => Assert.Contains("n'est plus sur le téléphone", f.Error));
            }
        }

        [Fact]
        public void Quelques_refus_isoles_ne_bloquent_pas_la_copie()
        {
            // Fantômes : entrées que le téléphone liste encore alors que le fichier n'existe plus.
            var ghosts = new HashSet<string> { "DCIM/Camera/IMG_3.jpg", "DCIM/Camera/IMG_7.jpg" };
            using (var phone = new RefusingPhone(new FolderPhoneSource(phoneDir), ghosts.Contains))
            {
                var scan = new ScanJob();
                scan.Run(new FolderPhoneSource(phoneDir), Drive(), CancellationToken.None);
                var job = new TransferJob();
                job.Run(phone, Drive(), scan.Files.Values.ToList(), scan.Index);

                Assert.Equal("done", job.State);
                Assert.Equal(22, job.Copied);
                Assert.Equal(2, job.Failed.Count);
            }
        }
    }
}
