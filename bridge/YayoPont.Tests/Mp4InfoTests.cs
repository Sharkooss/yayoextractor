using System.IO;
using System.Linq;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public class Mp4InfoTests
    {
        private static bool? Complete(byte[] data) => Mp4Info.IsComplete(new MemoryStream(data));

        [Fact]
        public void Complet_quand_les_boites_couvrent_le_fichier_et_que_moov_est_la()
        {
            Assert.True(Complete(MediaFactory.Mp4()));
            Assert.True(Complete(MediaFactory.Mp4(moovFirst: false)));
        }

        [Fact]
        public void Tronque_ou_sans_moov_est_incomplet()
        {
            var full = MediaFactory.Mp4(moovFirst: false);
            Assert.False(Complete(full.Take(full.Length - 10).ToArray()));
            var noMoov = MediaFactory.Box("ftyp", new byte[12]).Concat(MediaFactory.Box("mdat", new byte[100])).ToArray();
            Assert.False(Complete(noMoov));
            Assert.False(Complete(full.Concat(new byte[3]).ToArray())); // octets en trop
        }

        [Fact]
        public void Taille_zero_et_taille_64_bits()
        {
            var ftyp = MediaFactory.Box("ftyp", new byte[12]);
            var moov = MediaFactory.Box("moov", MediaFactory.Mvhd(2));
            var mdatToEnd = MediaFactory.BE32(0).Concat(System.Text.Encoding.ASCII.GetBytes("mdat")).Concat(new byte[50]).ToArray();
            Assert.True(Complete(ftyp.Concat(moov).Concat(mdatToEnd).ToArray()));

            var large = MediaFactory.BE32(1).Concat(System.Text.Encoding.ASCII.GetBytes("mdat"))
                .Concat(MediaFactory.BE32(0)).Concat(MediaFactory.BE32(16 + 40)).Concat(new byte[40]).ToArray();
            Assert.True(Complete(ftyp.Concat(large).Concat(moov).ToArray()));
        }

        [Fact]
        public void Ce_qui_n_est_pas_un_mp4_n_est_pas_juge()
        {
            Assert.Null(Complete(new byte[4096]));
            Assert.Null(Complete(Enumerable.Repeat((byte)9, 8000).ToArray()));
            Assert.Null(Complete(new byte[3]));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        public void Duree_lue_dans_mvhd(int version)
        {
            var moov = Mp4Info.ReadMoov(new MemoryStream(MediaFactory.Mp4(2.5, mvhdVersion: version, moovFirst: false)));
            Assert.True(Mp4Info.TryDuration(moov, out var seconds));
            Assert.Equal(2.5, seconds, 3);
        }

        [Fact]
        public void Lieu_iso6709_dans_udta()
        {
            var moov = Mp4Info.ReadMoov(new MemoryStream(MediaFactory.Mp4(location: "+45.7640+004.8357/")));
            Assert.True(Mp4Info.TryLocation(moov, out var lat, out var lon));
            Assert.Equal(45.764, lat, 4);
            Assert.Equal(4.8357, lon, 4);
            Assert.False(Mp4Info.TryLocation(Mp4Info.ReadMoov(new MemoryStream(MediaFactory.Mp4())), out _, out _));
        }

        [Fact]
        public void Moov_en_tete_lu_sans_tout_lire_sinon_rien()
        {
            var fast = MediaFactory.Mp4(location: "-33.8688+151.2093+010.000/", mdatBytes: 600 * 1024);
            var moov = Mp4Info.ReadMoovFromHead(new MemoryStream(fast), 512 * 1024);
            Assert.True(Mp4Info.TryLocation(moov, out var lat, out var lon));
            Assert.Equal(-33.8688, lat, 4);
            Assert.Equal(151.2093, lon, 4);

            var slow = MediaFactory.Mp4(location: "+1.0+2.0/", mdatBytes: 600 * 1024, moovFirst: false);
            Assert.Null(Mp4Info.ReadMoovFromHead(new MemoryStream(slow), 512 * 1024));
            Assert.Null(Mp4Info.ReadMoovFromHead(new MemoryStream(new byte[100]), 512 * 1024));
        }

        [Fact]
        public void Fichier_sur_disque()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, MediaFactory.Mp4(1.5));
                Assert.True(Mp4Info.IsComplete(path));
                Assert.Equal(1.5, Mp4Info.Duration(path).Value, 3);
                Assert.Null(Mp4Info.IsComplete(path + ".absent"));
            }
            finally { File.Delete(path); }
        }
    }
}
