using System;
using System.IO;

namespace YayoPont.Core
{
    /// <summary>Où ranger une photo sur la clé : Photos du téléphone\AAAA\MM - Mois.</summary>
    internal static class Destination
    {
        public const string RootFolderName = "Photos du téléphone";

        public static string Root(string driveRoot) => Path.Combine(driveRoot, RootFolderName);

        public static string Folder(string driveRoot, DateTime date) =>
            Path.Combine(Root(driveRoot), date.Year.ToString("0000"), $"{date.Month:00} - {Layout.Months[date.Month - 1]}");

        /// <summary>« nom.ext », sinon « nom (2).ext », « nom (3).ext »…</summary>
        public static string FreeName(string fileName, Func<string, bool> exists)
        {
            if (!exists(fileName)) return fileName;
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (int n = 2; ; n++)
            {
                var candidate = $"{stem} ({n}){ext}";
                if (!exists(candidate)) return candidate;
            }
        }

        /// <summary>« IMG_1 (2).jpg » → « IMG_1.jpg » : le nom avant la numérotation de FreeName.</summary>
        public static string WithoutNumber(string fileName)
        {
            var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
            var m = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*\S) \(\d+\)$");
            return m.Success ? m.Groups[1].Value + Path.GetExtension(fileName) : fileName;
        }

        public static bool HasNumber(string fileName) => !string.Equals(WithoutNumber(fileName), fileName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Destination choisie : une clé (ou un disque) par son id, ou un dossier du PC.
    /// Le « dossier racine géré » est le seul endroit où le pont écrit et range.
    /// </summary>
    internal sealed class Target
    {
        /// <summary>Identifie la destination pour vérifier que la copie suit bien l'analyse.</summary>
        public string Key { get; private set; }

        /// <summary>Racine de la destination : « E:\ » ou le dossier choisi.</summary>
        public string Root { get; private set; }

        /// <summary>Nom du dossier racine (« Photos du téléphone » par défaut, vide = directement dans la destination).</summary>
        public string RootName { get; private set; }

        public string ManagedRoot => RootName.Length == 0 ? Root : Path.Combine(Root, RootName);

        /// <summary>Ce que l'index des doublons parcourt : toute la clé USB, sinon le dossier géré.</summary>
        public string IndexRoot { get; private set; }

        public string Fs { get; private set; }

        public DriveDto Drive { get; private set; }

        public static Target ForDrive(DriveDto drive, string rootName)
        {
            var name = CleanRootName(rootName);
            var target = new Target
            {
                Key = "drive:" + drive.Id.ToUpperInvariant(),
                Root = drive.Root,
                RootName = name,
                Fs = drive.Fs,
                Drive = drive,
            };
            target.IndexRoot = drive.Kind == "fixed" ? target.ManagedRoot : drive.Root;
            return target;
        }

        /// <summary>Dossier du PC : chemin absolu sur une lettre de lecteur local, hors de Windows et des Program Files.</summary>
        public static Target ForFolder(string folder, string rootName, out string error)
        {
            var path = ValidateFolder(folder, out error);
            if (path == null) return null;
            string fs = null;
            try { fs = new DriveInfo(Path.GetPathRoot(path)).DriveFormat; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var target = new Target
            {
                Key = "folder:" + path.ToLowerInvariant(),
                Root = path,
                RootName = CleanRootName(rootName),
                Fs = fs,
            };
            target.IndexRoot = target.ManagedRoot;
            return target;
        }

        public static string CleanRootName(string rootName) =>
            rootName == null ? Destination.RootFolderName : Layout.RelativePath(rootName);

        internal static string ValidateFolder(string folder, out string error)
        {
            error = "Choisis un dossier du PC (chemin complet, par exemple D:\\Photos).";
            if (string.IsNullOrWhiteSpace(folder)) return null;
            folder = folder.Trim().Replace('/', '\\');
            if (folder.Length < 3) return null;
            char letter = char.ToUpperInvariant(folder[0]);
            if (letter < 'A' || letter > 'Z' || folder[1] != ':' || folder[2] != '\\') return null;
            // Noms courts 8.3 (« PROGRA~1 ») : ils contourneraient la liste des dossiers interdits.
            if (System.Text.RegularExpressions.Regex.IsMatch(folder, @"~\d")) return null;
            string full;
            try { full = Path.GetFullPath(folder); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
            if (!full.StartsWith(folder.Substring(0, 3), StringComparison.OrdinalIgnoreCase)) return null;
            full = full.Length > 3 ? full.TrimEnd('\\') : full;

            DriveInfo drive;
            try { drive = new DriveInfo(full.Substring(0, 1)); }
            catch (ArgumentException) { return null; }
            if (drive.DriveType == DriveType.Network)
            {
                error = "Les lecteurs réseau ne sont pas acceptés : choisis un dossier sur ce PC.";
                return null;
            }
            if (drive.DriveType == DriveType.NoRootDirectory || drive.DriveType == DriveType.CDRom || !drive.IsReady)
            {
                error = "Ce lecteur est introuvable.";
                return null;
            }
            foreach (var forbidden in ForbiddenFolders())
            {
                if (Inside(full, forbidden))
                {
                    error = "Ce dossier est réservé à Windows : choisis-en un autre.";
                    return null;
                }
            }
            error = null;
            return full;
        }

        private static string[] ForbiddenFolders() => new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
        };

        internal static bool Inside(string path, string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            folder = folder.TrimEnd('\\');
            return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Chemin relatif à la racine de la destination, avec « / ».</summary>
        public string Relative(string path)
        {
            var root = Root.EndsWith("\\") ? Root : Root + "\\";
            return (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path).Replace('\\', '/');
        }
    }
}
