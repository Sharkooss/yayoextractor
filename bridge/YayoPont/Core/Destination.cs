using System;
using System.IO;

namespace YayoPont.Core
{
    /// <summary>Où ranger une photo sur la clé : Photos du téléphone\AAAA\MM - Mois.</summary>
    internal static class Destination
    {
        public const string RootFolderName = "Photos du téléphone";

        private static readonly string[] Months =
        {
            "Janvier", "Février", "Mars", "Avril", "Mai", "Juin",
            "Juillet", "Août", "Septembre", "Octobre", "Novembre", "Décembre",
        };

        public static string Root(string driveRoot) => Path.Combine(driveRoot, RootFolderName);

        public static string Folder(string driveRoot, DateTime date) =>
            Path.Combine(Root(driveRoot), date.Year.ToString("0000"), $"{date.Month:00} - {Months[date.Month - 1]}");

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
    }
}
