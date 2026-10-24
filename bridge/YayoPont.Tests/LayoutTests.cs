using System;
using System.Collections.Generic;
using System.IO;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public class LayoutTests
    {
        private static LayoutValues Values(DateTime? date = null, string name = "IMG_1234.jpg", string source = "camera",
            string kind = "photo", Dictionary<string, object> place = null) =>
            new LayoutValues { Date = date, Name = name, Source = source, Kind = kind, Place = place };

        private static readonly DateTime Date = new DateTime(2026, 10, 24, 14, 30, 5);

        [Fact]
        public void Modele_par_defaut_identique_au_mode_normal()
        {
            Assert.Equal(@"2026\10 - Octobre", Layout.Folder(null, Values(Date)));
            Assert.Equal(@"2026\10 - Octobre", Layout.Folder(Layout.DefaultFolder, Values(Date)));
            Assert.Equal("IMG_1234.jpg", Layout.FileName(null, Values(Date)));
        }

        [Fact]
        public void Tous_les_jetons()
        {
            var place = new Dictionary<string, object>
            {
                ["lieu"] = "Lyon", ["ville"] = "Lyon", ["departement"] = "Rhône", ["region"] = "Auvergne-Rhône-Alpes", ["pays"] = "France",
            };
            var v = Values(Date, "VID-1.mp4", "whatsapp", "video", place);
            Assert.Equal(@"2026\10\24\2026-10-24\14h30", Layout.Folder("{annee}/{mois}/{jour}/{date}/{heure}", v));
            Assert.Equal(@"WhatsApp\Vidéos\Octobre", Layout.Folder("{source}/{type}/{MOIS_NOM}", v));
            Assert.Equal(@"France\Auvergne-Rhône-Alpes\Rhône\Lyon\Lyon", Layout.Folder("{pays}/{region}/{departement}/{ville}/{lieu}", v));
            Assert.Equal("2026-10-24 VID-1.mp4", Layout.FileName("{date} {nom}", v));
            Assert.Equal(@"{inconnu}", Layout.Folder("{inconnu}", v));
        }

        [Fact]
        public void Valeurs_manquantes()
        {
            var v = Values(null, source: "other");
            Assert.Equal(@"Date inconnue\Lieu inconnu\Pays inconnu\Autres", Layout.Folder("{annee}/{lieu}/{pays}/{source}", v));
            Assert.Equal(@"Captures d'écran\Photos", Layout.Folder("{source}/{type}", Values(Date, source: "screenshots")));
        }

        [Fact]
        public void Lieu_se_rabat_sur_la_ville()
        {
            var v = Values(Date, place: new Dictionary<string, object> { ["ville"] = "Annecy" });
            Assert.Equal("Annecy", Layout.Folder("{lieu}", v));
        }

        [Fact]
        public void Tout_dans_un_dossier()
        {
            Assert.Equal("", Layout.Folder("", Values(Date)));
        }

        [Theory]
        [InlineData("a<b>c:d\"e|f?g*h", "a_b_c_d_e_f_g_h")]
        [InlineData("Vacances. . .", "Vacances")]
        [InlineData("  espaces  ", "espaces")]
        [InlineData("CON", "_CON")]
        [InlineData("com1.txt", "_com1.txt")]
        [InlineData("LPT9", "_LPT9")]
        [InlineData("..", "")]
        [InlineData(".", "")]
        [InlineData("CONSOLE", "CONSOLE")]
        public void Nettoyage_des_noms(string raw, string expected)
        {
            Assert.Equal(expected, Layout.Segment(raw));
        }

        [Fact]
        public void Quatre_vingts_caracteres_au_plus()
        {
            Assert.Equal(80, Layout.Segment(new string('x', 200)).Length);
            var name = Layout.FileName("{nom}", Values(Date, new string('y', 200) + ".jpeg"));
            Assert.Equal(80, name.Length);
            Assert.EndsWith(".jpeg", name);
        }

        [Fact]
        public void Jamais_hors_de_la_racine()
        {
            var v = Values(Date, place: new Dictionary<string, object> { ["ville"] = "..", ["pays"] = "C:" });
            var folder = Layout.Folder("../../{ville}/../{pays}/{annee}", v);
            Assert.Equal(@"C_\2026", folder);
            var root = @"E:\Photos du téléphone";
            Assert.StartsWith(root + @"\", Path.GetFullPath(Path.Combine(root, folder)));
            Assert.Equal("Photos", Layout.RelativePath("../Photos/.."));
            Assert.Equal(@"a\b", Layout.RelativePath(@"\a\\b\"));
        }

        [Fact]
        public void Le_nom_garde_toujours_son_extension()
        {
            Assert.Equal("Lyon.HEIC", Layout.FileName("{ville}", Values(Date, "IMG_1.HEIC", place: new Dictionary<string, object> { ["ville"] = "Lyon" })));
            Assert.Equal("IMG_1.jpg", Layout.FileName("   ", Values(Date, "IMG_1.jpg")));
            Assert.Equal("a b.jpg", Layout.FileName("a/b", Values(Date, "IMG_1.jpg")));
        }

        [Fact]
        public void Numerotation_retiree()
        {
            Assert.Equal("IMG_1.jpg", Destination.WithoutNumber("IMG_1 (2).jpg"));
            Assert.Equal("IMG_1.jpg", Destination.WithoutNumber("IMG_1.jpg"));
            Assert.True(Destination.HasNumber("a (12).mp4"));
            Assert.False(Destination.HasNumber("(2).mp4"));
        }
    }
}
