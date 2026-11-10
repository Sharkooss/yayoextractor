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

        /// <summary>Fichiers que le téléphone ne livre jamais (appel sans réponse), comme ceux de l'S20 FE.</summary>
        private sealed class SilentPhone : IPhoneSource
        {
            private readonly IPhoneSource inner;
            private readonly Func<string, bool> silent;
            public int Opened;

            public SilentPhone(IPhoneSource inner, Func<string, bool> silent)
            {
                this.inner = inner;
                this.silent = silent;
            }

            public IList<PhoneInfo> Phones() => inner.Phones();
            public IList<PhoneFile> Enumerate(string phoneId, Action<PhoneFile> found, CancellationToken cancel) => inner.Enumerate(phoneId, found, cancel);
            public byte[] Thumbnail(string id) => inner.Thumbnail(id);
            public bool UsbSeen() => inner.UsbSeen();
            public void Dispose() => inner.Dispose();

            public Stream OpenRead(string id)
            {
                Opened++;
                if (silent(id)) throw PhoneReadException.NoAnswer();
                return inner.OpenRead(id);
            }
        }

        /// <summary>Téléphone « occupé » (0x800700AA) pour certains fichiers : une lecture précédente n'est pas finie.</summary>
        private sealed class BusyPhone : IPhoneSource
        {
            private readonly IPhoneSource inner;
            private readonly Func<string, bool> busy;
            public int Opened;

            public BusyPhone(IPhoneSource inner, Func<string, bool> busy)
            {
                this.inner = inner;
                this.busy = busy;
            }

            public IList<PhoneInfo> Phones() => inner.Phones();
            public IList<PhoneFile> Enumerate(string phoneId, Action<PhoneFile> found, CancellationToken cancel) => inner.Enumerate(phoneId, found, cancel);
            public byte[] Thumbnail(string id) => inner.Thumbnail(id);
            public bool UsbSeen() => inner.UsbSeen();
            public void Dispose() => inner.Dispose();

            public Stream OpenRead(string id)
            {
                Opened++;
                if (busy(id)) throw PhoneReadException.Refused(PhoneReadException.BusyCode);
                return inner.OpenRead(id);
            }
        }

        [Fact]
        public void La_copie_s_arrete_apres_trois_occupes_de_suite_avec_la_marche_a_suivre()
        {
            var scan = new ScanJob();
            scan.Run(new FolderPhoneSource(phoneDir), Drive(), CancellationToken.None);
            var files = scan.Files.Values.Where(f => f.Source == "camera").OrderBy(f => f.Name).ToList();

            // Les quatre premiers passent, puis le téléphone reste occupé.
            var done = new HashSet<string>(files.Take(4).Select(f => f.Id));
            using (var phone = new BusyPhone(new FolderPhoneSource(phoneDir), id => !done.Contains(id)))
            {
                var job = new TransferJob();
                job.Run(phone, Drive(), files, scan.Index);
                Assert.Equal("error", job.State);
                Assert.Equal(4, job.Copied);
                Assert.Equal(3, job.Failed.Count);
                Assert.Contains("0x800700AA", job.Error);
                Assert.Contains("4 fichiers déjà copiés", job.Error);
                Assert.Contains("occupé", job.Detail);
            }
        }

        [Fact]
        public void Un_occupe_isole_ne_bloque_pas_la_copie_et_ne_marque_rien_d_illisible()
        {
            using (var phone = new BusyPhone(new FolderPhoneSource(phoneDir), id => id.EndsWith("IMG_5.jpg") || id.Contains("WhatsApp")))
            {
                var scan = new ScanJob();
                scan.Run(phone, Drive(), CancellationToken.None);
                Assert.False(scan.ReadBlocked);
                Assert.All(scan.Items, i => Assert.False(i.Unreadable));

                var job = new TransferJob();
                job.Run(phone, Drive(), scan.Files.Values.Where(f => f.Source == "camera").ToList(), scan.Index);
                Assert.Equal("done", job.State);
                Assert.Single(job.Failed);
                Assert.Equal(11, job.Copied);
            }
        }

        [Fact]
        public void La_comparaison_s_arrete_au_premier_appel_sans_reponse()
        {
            var same = Enumerable.Range(0, 20).Select(i => new PhoneFile { Id = $"DCIM/Camera/IMG_{i}.jpg", Name = $"IMG_{i}.jpg", Size = 2000 }).ToList();
            using (var phone = new SilentPhone(new FolderPhoneSource(phoneDir), _ => true))
            {
                ScanJob.MarkDuplicates(phone, same, CancellationToken.None);
                Assert.Equal(1, phone.Opened);
            }
        }

        [Fact]
        public void Un_appel_sans_reponse_pendant_l_essai_de_lecture_ne_marque_rien_d_illisible()
        {
            using (var phone = new SilentPhone(new FolderPhoneSource(phoneDir), id => id.Contains("WhatsApp")))
            {
                var scan = new ScanJob();
                scan.Run(phone, Drive(), CancellationToken.None);

                Assert.False(scan.ReadBlocked);
                Assert.Empty(scan.BlockedSources);
                Assert.All(scan.Items, i => Assert.False(i.Unreadable));
            }
        }

        [Fact]
        public void La_copie_passe_un_fichier_sans_reponse_mais_s_arrete_apres_trois_de_suite()
        {
            var scan = new ScanJob();
            scan.Run(new FolderPhoneSource(phoneDir), Drive(), CancellationToken.None);
            var files = scan.Files.Values.Where(f => f.Source == "camera").OrderBy(f => f.Name).ToList();

            using (var phone = new SilentPhone(new FolderPhoneSource(phoneDir), id => id.EndsWith("IMG_3.jpg")))
            {
                var job = new TransferJob();
                job.Run(phone, Drive(), files, scan.Index);
                Assert.Equal("done", job.State);
                Assert.Single(job.Failed);
                Assert.Equal(11, job.Copied);
            }

            using (var phone = new SilentPhone(new FolderPhoneSource(phoneDir), _ => true))
            {
                var job = new TransferJob();
                job.Run(phone, Drive(), files, scan.Index);
                Assert.Equal("error", job.State);
                Assert.Equal(3, job.Failed.Count);
                Assert.Equal(3, phone.Opened);
            }
        }

        [Fact]
        public void La_comparaison_des_doublons_s_arrete_au_bout_de_son_budget()
        {
            // Mêmes tailles partout : tout serait lu. Budget nul : rien n'est lu, l'analyse continue quand même.
            var same = Enumerable.Range(0, 6).Select(i => new PhoneFile { Id = $"DCIM/Camera/IMG_{i}.jpg", Name = $"IMG_{i}.jpg", Size = 2000 }).ToList();
            using (var phone = new RefusingPhone(new FolderPhoneSource(phoneDir), _ => false))
            {
                ScanJob.MarkDuplicates(phone, same, CancellationToken.None, null, TimeSpan.Zero);
                Assert.Equal(0, phone.Opened);
                Assert.All(same, f => Assert.Null(f.DuplicateOf));
            }
        }

        [Fact]
        public void La_comparaison_cesse_de_lire_apres_des_fichiers_illisibles_de_suite()
        {
            var same = Enumerable.Range(0, 30).Select(i => new PhoneFile { Id = $"DCIM/Camera/IMG_{i}.jpg", Name = $"IMG_{i}.jpg", Size = 2000 }).ToList();
            int reads = 0;
            using (var phone = new GonePhone(new FolderPhoneSource(phoneDir), _ => { reads++; return true; }))
            {
                ScanJob.MarkDuplicates(phone, same, CancellationToken.None);
                Assert.Equal(ScanJob.MaxHeadMisses, reads);
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
