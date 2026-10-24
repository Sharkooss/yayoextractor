using System;
using System.IO;
using System.Linq;
using System.Threading;
using YayoPont.Core;
using YayoPont.Phone;
using Xunit;

namespace YayoPont.Tests
{
    public sealed class GalleryRulesTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "yayo-galerie-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        private void Write(string relative, byte[] data = null, string under = null)
        {
            var path = Path.Combine(under ?? Path.Combine(root, "tel1"), relative.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data ?? new byte[10]);
        }

        [Theory]
        [InlineData("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Images/Sent", true)]
        [InlineData("WhatsApp/Media/WhatsApp Video/Sent/Vieux", true)]
        [InlineData("WhatsApp/Media/WhatsApp Images", false)]
        [InlineData("DCIM/Sent", false)]
        [InlineData("", false)]
        public void Dossier_Sent_de_WhatsApp(string path, bool hidden)
        {
            Assert.Equal(hidden, MediaRules.HiddenFolder(path));
        }

        [Fact]
        public void Nomedia_cache_le_dossier_et_ses_sous_dossiers_meme_liste_apres_les_photos()
        {
            Write("DCIM/Camera/IMG_1.jpg");
            Write("Pictures/Private/a.jpg");
            Write("Pictures/Private/Sous/b.jpg");
            Write("Pictures/Private/.nomedia"); // listé avec (ou après) les photos du dossier
            Write("WhatsApp/Media/WhatsApp Images/IMG-1.jpg");
            Write("WhatsApp/Media/WhatsApp Images/Sent/IMG-2.jpg");
            Write("DCIM/Camera/.trashed-1700000000-IMG_3.jpg");
            Write("DCIM/.thumbnails/t.jpg");
            using (var source = new FolderPhoneSource(Path.Combine(root, "tel1")))
            {
                var files = source.Enumerate(_ => { }, CancellationToken.None).ToDictionary(f => f.Name);
                Assert.False(files["IMG_1.jpg"].Hidden);
                Assert.True(files["a.jpg"].Hidden);
                Assert.True(files["b.jpg"].Hidden);
                Assert.False(files["IMG-1.jpg"].Hidden);
                Assert.True(files["IMG-2.jpg"].Hidden);
                Assert.True(files[".trashed-1700000000-IMG_3.jpg"].Hidden);
                Assert.False(files.ContainsKey("t.jpg")); // dossiers cachés toujours ignorés
                Assert.Equal("Pictures/Private/Sous", files["b.jpg"].Folder);
            }
        }

        [Fact]
        public void Plusieurs_telephones_simules()
        {
            Write("DCIM/Camera/A.jpg");
            Write("DCIM/Camera/B.jpg", under: Path.Combine(root, "tel2"));
            Write("DCIM/Camera/C.jpg", under: Path.Combine(root, "tel2"));
            using (var source = new FolderPhoneSource(new[] { Path.Combine(root, "tel1"), Path.Combine(root, "tel2") }))
            {
                var phones = source.Phones();
                Assert.Equal(new[] { "test-1", "test-2" }, phones.Select(p => p.Id).ToArray());
                Assert.Contains("tel2", phones[1].Name);
                var second = source.Enumerate("test-2", _ => { }, CancellationToken.None);
                Assert.Equal(new[] { "B.jpg", "C.jpg" }, second.Select(f => f.Name).OrderBy(n => n).ToArray());
                using (var stream = source.OpenRead(second[0].Id)) Assert.Equal(10, stream.Length); // lit sur le téléphone choisi
                Assert.Single(source.Enumerate(_ => { }, CancellationToken.None)); // null = le premier
                Assert.Throws<IOException>(() => source.Enumerate("test-9", _ => { }, CancellationToken.None));
            }
        }
    }
}
