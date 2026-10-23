using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using YayoPont.Drives;
using YayoPont.Phone;
using Xunit;

namespace YayoPont.Tests
{
    public sealed class PhoneSourceTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "yayo-phone-" + Guid.NewGuid().ToString("N"));

        public PhoneSourceTests()
        {
            Write("DCIM/Camera/20261021_101010.jpg", Jpeg(800, 600));
            Write("DCIM/Screenshots/Screenshot_1.png", new byte[100]);
            Write("DCIM/.thumbnails/t.jpg", new byte[10]);
            Write("Android/data/com.app/cache.jpg", new byte[10]);
            Write("Android/media/com.whatsapp/WhatsApp/Media/WhatsApp Video/VID-1.mp4", new byte[300]);
            Write("Documents/notes.txt", new byte[10]);
        }

        public void Dispose() => Directory.Delete(root, true);

        private void Write(string relative, byte[] data)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
        }

        internal static byte[] Jpeg(int w, int h)
        {
            using (var bmp = new Bitmap(w, h))
            using (var g = Graphics.FromImage(bmp))
            using (var ms = new MemoryStream())
            {
                g.Clear(Color.Orange);
                bmp.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }
        }

        [Fact]
        public void Enumere_les_medias_avec_provenance_et_regles()
        {
            using (var source = new FolderPhoneSource(root))
            {
                Assert.Equal("ready", source.Detect().State);
                var files = source.Enumerate(_ => { }, CancellationToken.None).OrderBy(f => f.Name).ToList();
                Assert.Equal(new[] { "20261021_101010.jpg", "Screenshot_1.png", "VID-1.mp4" }, files.Select(f => f.Name).ToArray());
                Assert.Equal(new[] { "camera", "screenshots", "whatsapp" }, files.Select(f => f.Source).ToArray());
                Assert.Equal(new[] { "photo", "photo", "video" }, files.Select(f => f.Kind).ToArray());
                Assert.All(files, f => Assert.NotNull(f.Date));
                Assert.Equal(300, files[2].Size);
            }
        }

        [Fact]
        public void Lit_un_fichier_et_produit_une_vignette()
        {
            using (var source = new FolderPhoneSource(root))
            {
                var photo = source.Enumerate(_ => { }, CancellationToken.None).First(f => f.Name.EndsWith(".jpg"));
                using (var stream = source.OpenRead(photo.Id))
                    Assert.Equal(photo.Size, ReadAll(stream).Length);
                var thumb = source.Thumbnail(photo.Id);
                using (var img = Image.FromStream(new MemoryStream(thumb)))
                {
                    Assert.True(img.Width <= 320 && img.Height <= 320);
                    Assert.Equal(320, img.Width);
                }
            }
        }

        [Fact]
        public void Refuse_de_sortir_du_dossier_simule()
        {
            using (var source = new FolderPhoneSource(root))
                Assert.Throws<FileNotFoundException>(() => source.OpenRead("../../Windows/win.ini"));
        }

        [Fact]
        public void Telephone_verrouille_et_absent_en_simulation()
        {
            File.WriteAllText(Path.Combine(root, "VERROUILLE"), "");
            using (var source = new FolderPhoneSource(root)) Assert.Equal("locked", source.Detect().State);
            using (var source = new FolderPhoneSource(Path.Combine(root, "absent"))) Assert.Null(source.Detect());
        }

        [Theory]
        [InlineData(@"USB\VID_04E8&PID_6860\R58N", true)]   // Samsung
        [InlineData(@"USB\VID_18D1&PID_4EE1\X", true)]      // Google
        [InlineData(@"USB\VID_05AC&PID_12A8\X", true)]      // iPhone
        [InlineData(@"USB\VID_05AC&PID_024F\X", false)]     // clavier Apple
        [InlineData(@"USB\VID_046D&PID_C52B\X", false)]     // souris Logitech
        [InlineData(@"USB\ROOT_HUB30\4&1", false)]
        public void Reconnait_un_telephone_branche_en_usb(string instanceId, bool expected)
        {
            bool isPhone = Native.TryParseVidPid(instanceId, out int vid, out int pid) && PhoneUsb.IsPhone(vid, pid);
            Assert.Equal(expected, isPhone);
        }

        private static byte[] ReadAll(Stream s)
        {
            using (var ms = new MemoryStream()) { s.CopyTo(ms); return ms.ToArray(); }
        }
    }
}
