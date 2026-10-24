using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace YayoPont.Core
{
    /// <summary>Ce qu'on sait d'un fichier pour le ranger.</summary>
    internal sealed class LayoutValues
    {
        public DateTime? Date;
        public string Source;
        public string Kind;
        public string Name;
        public IDictionary<string, object> Place;
    }

    /// <summary>
    /// Rangement par modèle : « {annee}/{mois} - {mois_nom} » donne « 2026\10 - Octobre ».
    /// Chaque dossier produit est nettoyé pour Windows ; le résultat reste toujours
    /// un chemin relatif sans « .. », donc sous la racine de la destination.
    /// </summary>
    internal static class Layout
    {
        public const string DefaultFolder = "{annee}/{mois} - {mois_nom}";
        public const string DefaultFileName = "{nom}";
        public const int MaxSegment = 80;

        internal static readonly string[] Months =
        {
            "Janvier", "Février", "Mars", "Avril", "Mai", "Juin",
            "Juillet", "Août", "Septembre", "Octobre", "Novembre", "Décembre",
        };

        private static readonly Regex Token = new Regex(@"\{([a-z_]+)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

        /// <summary>Dossier relatif (séparateur « \ »), vide = directement à la racine.</summary>
        public static string Folder(string template, LayoutValues values)
        {
            var segments = Split(template ?? DefaultFolder).Select(s => Segment(Render(s, values))).Where(s => s.Length > 0);
            return string.Join("\\", segments);
        }

        /// <summary>Nom de fichier : modèle rendu et nettoyé, extension d'origine toujours gardée.</summary>
        public static string FileName(string template, LayoutValues values)
        {
            var ext = Segment(Path.GetExtension(values.Name ?? ""));
            if (ext.Length > 0 && ext[0] != '.') ext = "";
            var stem = Segment(Render((template ?? DefaultFileName).Replace('/', ' ').Replace('\\', ' '), values));
            if (stem.Length == 0) stem = Segment(Path.GetFileNameWithoutExtension(values.Name ?? ""));
            if (stem.Length == 0) stem = "Sans nom";
            if (stem.Length + ext.Length > MaxSegment) stem = Trim(stem.Substring(0, Math.Max(1, MaxSegment - ext.Length)));
            return stem + ext;
        }

        /// <summary>Chemin relatif libre (nom du dossier racine) : chaque partie nettoyée, les vides retirées.</summary>
        public static string RelativePath(string path) =>
            string.Join("\\", Split(path ?? "").Select(Segment).Where(s => s.Length > 0));

        /// <summary>Un nom de dossier ou de fichier acceptable par Windows.</summary>
        public static string Segment(string raw)
        {
            var sb = new StringBuilder(raw?.Length ?? 0);
            foreach (var c in raw ?? "") sb.Append(c < 0x20 || Array.IndexOf(Invalid, c) >= 0 ? '_' : c);
            var name = Trim(sb.ToString());
            if (name.Length > MaxSegment) name = Trim(name.Substring(0, MaxSegment));
            var stem = name.Split('.')[0].TrimEnd(' ');
            if (Reserved.Contains(stem)) name = "_" + name;
            return name;
        }

        private static string Trim(string s) => s.Trim(' ').TrimEnd('.', ' ');

        private static IEnumerable<string> Split(string template) => template.Split('/', '\\');

        private static string Render(string template, LayoutValues v) =>
            Token.Replace(template, m => Value(m.Groups[1].Value.ToLowerInvariant(), v) ?? m.Value);

        private static string Value(string token, LayoutValues v)
        {
            var d = v.Date;
            const string noDate = "Date inconnue";
            switch (token)
            {
                case "annee": return d?.Year.ToString("0000") ?? noDate;
                case "mois": return d?.Month.ToString("00") ?? noDate;
                case "mois_nom": return d.HasValue ? Months[d.Value.Month - 1] : noDate;
                case "jour": return d?.Day.ToString("00") ?? noDate;
                case "date": return d?.ToString("yyyy-MM-dd") ?? noDate;
                case "heure": return d.HasValue ? $"{d.Value.Hour:00}h{d.Value.Minute:00}" : "Heure inconnue";
                case "source": return SourceName(v.Source);
                case "type": return v.Kind == "video" ? "Vidéos" : "Photos";
                case "nom": return Path.GetFileNameWithoutExtension(v.Name ?? "");
                case "lieu": return Place(v, "lieu") ?? Place(v, "ville") ?? "Lieu inconnu";
                case "ville": return Place(v, "ville") ?? "Ville inconnue";
                case "departement": return Place(v, "departement") ?? "Département inconnu";
                case "region": return Place(v, "region") ?? "Région inconnue";
                case "pays": return Place(v, "pays") ?? "Pays inconnu";
                default: return null; // jeton inconnu : laissé tel quel
            }
        }

        private static string Place(LayoutValues v, string key) =>
            v.Place != null && v.Place.TryGetValue(key, out var value) && value is string s && !string.IsNullOrWhiteSpace(s) ? s : null;

        public static string SourceName(string source)
        {
            switch (source)
            {
                case "camera": return "Appareil photo";
                case "whatsapp": return "WhatsApp";
                case "screenshots": return "Captures d'écran";
                default: return "Autres";
            }
        }
    }
}
