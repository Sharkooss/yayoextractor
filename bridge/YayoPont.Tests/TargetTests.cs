using System;
using System.IO;
using YayoPont.Core;
using Xunit;

namespace YayoPont.Tests
{
    public class TargetTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Photos")]
        [InlineData(@"\Photos")]
        [InlineData(@"\\serveur\partage")]
        [InlineData(@"\\?\C:\Photos")]
        [InlineData(@"C:Photos")]
        [InlineData(@"1:\Photos")]
        [InlineData(@"C:\PROGRA~1\x")]
        public void Refuse_ce_qui_n_est_pas_un_chemin_local_absolu(string folder)
        {
            Assert.Null(Target.ValidateFolder(folder, out var error));
            Assert.NotNull(error);
        }

        [Fact]
        public void Refuse_windows_et_program_files()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Assert.Null(Target.ValidateFolder(windows, out _));
            Assert.Null(Target.ValidateFolder(Path.Combine(windows, "Temp"), out _));
            Assert.Null(Target.ValidateFolder(Path.Combine(Path.GetTempPath(), @"..\..\..\..\..\..\..\Windows\System32"), out _));
            Assert.Null(Target.ValidateFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Yayo"), out _));
            Assert.Null(Target.ValidateFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), out _));
        }

        [Fact]
        public void Accepte_un_dossier_du_pc_et_normalise()
        {
            var temp = Path.GetTempPath();
            var path = Target.ValidateFolder(temp.Replace('\\', '/') + "Yayo/../Yayo2/", out var error);
            Assert.Null(error);
            Assert.Equal(Path.Combine(Path.GetFullPath(temp), "Yayo2"), path);
            var target = Target.ForFolder(temp + "Yayo2", "", out _);
            Assert.Equal(target.Root, target.ManagedRoot);
            Assert.Equal(target.ManagedRoot, target.IndexRoot);
        }

        [Fact]
        public void Cle_usb_index_sur_toute_la_cle_disque_fixe_sur_le_dossier_gere()
        {
            var usb = Target.ForDrive(new DriveDto { Id = "e", Root = @"E:\", Kind = "usb" }, null);
            Assert.Equal(@"E:\Photos du téléphone", usb.ManagedRoot);
            Assert.Equal(@"E:\", usb.IndexRoot);
            Assert.Equal("drive:E", usb.Key);
            var disk = Target.ForDrive(new DriveDto { Id = "D", Root = @"D:\", Kind = "fixed" }, "Sauvegardes/../Téléphone");
            Assert.Equal(@"D:\Sauvegardes\Téléphone", disk.ManagedRoot);
            Assert.Equal(disk.ManagedRoot, disk.IndexRoot);
            Assert.Equal("Photos du téléphone/2026/a.jpg", usb.Relative(@"E:\Photos du téléphone\2026\a.jpg"));
        }
    }
}
