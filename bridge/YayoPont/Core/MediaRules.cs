using System;
using System.Collections.Generic;
using System.IO;

namespace YayoPont.Core
{
    /// <summary>Ce qui compte comme photo ou vidéo, et d'où ça vient sur le téléphone.</summary>
    internal static class MediaRules
    {
        public const int MaxDepth = 10;

        private static readonly HashSet<string> PhotoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".gif", ".dng",
        };

        private static readonly HashSet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mov", ".3gp", ".mkv", ".webm", ".avi", ".m4v",
        };

        /// <summary>« photo », « video » ou null si le fichier n'est pas un média.</summary>
        public static string Kind(string fileName)
        {
            var ext = Path.GetExtension(fileName ?? "");
            if (PhotoExtensions.Contains(ext)) return "photo";
            if (VideoExtensions.Contains(ext)) return "video";
            return null;
        }

        /// <summary>
        /// Provenance d'après le chemin relatif au stockage (séparateur « / »).
        /// Les captures passent avant DCIM : Android 11+ les range dans DCIM/Screenshots.
        /// </summary>
        public static string Source(string relativePath)
        {
            var path = (relativePath ?? "").Replace('\\', '/').ToLowerInvariant();
            if (path.Contains("screenshot")) return "screenshots";
            if (path.Contains("whatsapp")) return "whatsapp";
            if (path.StartsWith("dcim/") || path.Contains("/dcim/")) return "camera";
            return "other";
        }

        /// <summary>Fichier qui cache son dossier (et ses sous-dossiers) à la Galerie.</summary>
        public static bool IsNoMedia(string fileName) => string.Equals(fileName, ".nomedia", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Dossier « Sent » d'un chemin WhatsApp : copies (recompressées) de ce
        /// qu'on a envoyé, que la Galerie ne montre pas.
        /// </summary>
        public static bool HiddenFolder(string relativePath)
        {
            var path = (relativePath ?? "").Replace('\\', '/');
            if (path.IndexOf("whatsapp", StringComparison.OrdinalIgnoreCase) < 0) return false;
            foreach (var part in path.Split('/'))
            {
                if (part.Equals("Sent", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Fichier masqué (« .trashed-…-IMG.jpg » de la corbeille Android, « .pending-… ») :
        /// la Galerie ne le montre pas non plus.
        /// </summary>
        public static bool HiddenFile(string fileName) => (fileName ?? "").StartsWith(".");

        /// <summary>
        /// Dossiers à ne pas parcourir : cachés (vignettes, corbeille), données
        /// d'applications, caches et stickers. Android/media reste parcouru,
        /// WhatsApp y range ses photos depuis Android 11.
        /// </summary>
        public static bool SkipFolder(string name, string parentPath)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith(".")) return true;
            var lower = name.ToLowerInvariant();
            if (lower.Contains("thumbnail") || lower.Contains("cache") || lower.Contains("sticker")) return true;
            var parent = (parentPath ?? "").Replace('\\', '/').Trim('/');
            if (parent.Equals("android", StringComparison.OrdinalIgnoreCase) && (lower == "data" || lower == "obb")) return true;
            return false;
        }
    }
}
