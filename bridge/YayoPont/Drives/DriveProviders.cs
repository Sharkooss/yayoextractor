using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using YayoPont.Core;

namespace YayoPont.Drives
{
    internal interface IDriveProvider
    {
        IList<DriveDto> List();
        bool Eject(string id, out string message);
    }

    internal static class DriveActions
    {
        public static DriveDto Find(this IDriveProvider provider, string id) =>
            provider.List().FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

        /// <summary>Ouvre l'Explorateur sur « Photos du téléphone » (créé au besoin).</summary>
        public static void Open(DriveDto drive)
        {
            var folder = Destination.Root(drive.Root);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
    }

    /// <summary>
    /// Clés USB : lecteurs amovibles, plus les disques fixes branchés en USB
    /// (disques et SSD externes), jamais le disque de Windows.
    /// </summary>
    internal sealed class RealDriveProvider : IDriveProvider
    {
        public IList<DriveDto> List()
        {
            var system = Path.GetPathRoot(Environment.SystemDirectory);
            var drives = new List<DriveDto>();
            foreach (var info in DriveInfo.GetDrives())
            {
                try
                {
                    if (!info.IsReady) continue;
                    if (string.Equals(info.RootDirectory.FullName, system, StringComparison.OrdinalIgnoreCase)) continue;
                    var letter = info.Name.Substring(0, 1).ToUpperInvariant();
                    string product = null;
                    bool usb = info.DriveType == DriveType.Removable;
                    if (!usb && info.DriveType == DriveType.Fixed) usb = Native.IsUsbVolume(letter, out product);
                    else if (usb) Native.IsUsbVolume(letter, out product);
                    if (!usb) continue;
                    drives.Add(new DriveDto
                    {
                        Id = letter,
                        Letter = letter + ":",
                        Label = !string.IsNullOrWhiteSpace(info.VolumeLabel) ? info.VolumeLabel : (product ?? "Clé USB"),
                        Fs = info.DriveFormat,
                        TotalBytes = info.TotalSize,
                        FreeBytes = info.AvailableFreeSpace,
                        Root = info.RootDirectory.FullName,
                    });
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return drives;
        }

        public bool Eject(string id, out string message) => Native.Eject(id, out message);
    }

    /// <summary>Mode simulation : un dossier joue le rôle de la clé.</summary>
    internal sealed class FakeDriveProvider : IDriveProvider
    {
        private readonly string root;
        private bool ejected;

        public FakeDriveProvider(string root)
        {
            this.root = Path.GetFullPath(root);
            Directory.CreateDirectory(this.root);
        }

        public IList<DriveDto> List()
        {
            if (ejected) return new List<DriveDto>();
            var host = new DriveInfo(Path.GetPathRoot(root));
            return new List<DriveDto>
            {
                new DriveDto
                {
                    Id = "TEST",
                    Letter = "T:",
                    Label = "Clé de test",
                    Fs = "exFAT",
                    TotalBytes = host.TotalSize,
                    FreeBytes = host.AvailableFreeSpace,
                    Root = root,
                },
            };
        }

        public bool Eject(string id, out string message)
        {
            ejected = true;
            message = "Clé de test retirée (simulation).";
            return true;
        }
    }
}
