using System;
using System.IO;
using System.Linq;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public class DuplicateIndexTests
    {
        private static readonly byte[] HeadA = { 1, 2, 3, 4 };
        private static readonly byte[] HeadB = { 9, 9, 9, 9 };

        private static DuplicateIndex Index() => DuplicateIndex.Build(new[]
        {
            (@"E:\Photos du téléphone\2026\10 - Octobre\IMG_1.jpg", 1000L),
            (@"E:\Vacances\renommee.jpg", 2000L),
        });

        [Fact]
        public void Meme_nom_et_meme_taille_sans_lire_le_telephone()
        {
            int reads = 0;
            Assert.True(Index().IsOnDrive("img_1.JPG", 1000, () => { reads++; return HeadA; }, _ => HeadA));
            Assert.Equal(0, reads);
        }

        [Fact]
        public void Copie_renommee_reconnue_par_son_debut()
        {
            Assert.True(Index().IsOnDrive("20261021_101010.jpg", 2000, () => HeadA, _ => HeadA));
        }

        [Fact]
        public void Meme_taille_mais_contenu_different()
        {
            Assert.False(Index().IsOnDrive("autre.jpg", 2000, () => HeadA, _ => HeadB));
        }

        [Fact]
        public void Taille_differente_sans_lire_le_telephone()
        {
            int reads = 0;
            Assert.False(Index().IsOnDrive("IMG_1.jpg", 1001, () => { reads++; return HeadA; }, _ => HeadA));
            Assert.Equal(0, reads);
        }

        [Fact]
        public void Add_rend_visible_une_copie_toute_fraiche()
        {
            var index = Index();
            index.Add(@"E:\Photos du téléphone\2026\10 - Octobre\IMG_9.jpg", 5000);
            Assert.True(index.IsOnDrive("IMG_9.jpg", 5000, () => HeadA, _ => HeadA));
        }

        [Fact]
        public void FromDirectory_indexe_les_medias_et_saute_les_dossiers_systeme()
        {
            var root = Path.Combine(Path.GetTempPath(), "yayo-idx-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Photos", "2026"));
                Directory.CreateDirectory(Path.Combine(root, "System Volume Information"));
                Directory.CreateDirectory(Path.Combine(root, "$RECYCLE.BIN"));
                File.WriteAllBytes(Path.Combine(root, "Photos", "2026", "a.jpg"), new byte[10]);
                File.WriteAllBytes(Path.Combine(root, "b.mp4"), new byte[20]);
                File.WriteAllBytes(Path.Combine(root, "notes.txt"), new byte[30]);
                File.WriteAllBytes(Path.Combine(root, "System Volume Information", "c.jpg"), new byte[40]);
                File.WriteAllBytes(Path.Combine(root, "$RECYCLE.BIN", "d.jpg"), new byte[50]);

                var files = DuplicateIndex.MediaFiles(root).Select(f => Path.GetFileName(f.path)).OrderBy(n => n).ToArray();
                Assert.Equal(new[] { "a.jpg", "b.mp4" }, files);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
