using System;

namespace YayoPont.Core
{
    /// <summary>Clé USB (ou disque) proposée comme destination.</summary>
    internal sealed class DriveDto
    {
        public string Id { get; set; }
        public string Letter { get; set; }
        public string Label { get; set; }
        public string Fs { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }

        /// <summary>« usb » (clé, disque externe) ou « fixed » (disque interne non système, avec ?all=1).</summary>
        public string Kind { get; set; } = "usb";

        internal string Root;
    }

    /// <summary>Téléphone vu par Windows : « ready » si son stockage est lisible, « locked » sinon.</summary>
    internal sealed class PhoneInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Manufacturer { get; set; }
        public string State { get; set; }
    }

    /// <summary>Fichier média trouvé sur le téléphone.</summary>
    internal sealed class PhoneFile
    {
        public string Id;
        public string Name;
        public string RelativePath;
        public long Size;
        public DateTime? Date;
        public string Kind;
        public string Source;

        /// <summary>Dans un dossier que la Galerie ne montre pas (.nomedia, WhatsApp « Sent », fichier caché).</summary>
        public bool Hidden;

        // Connus après l'analyse.
        public bool Live;
        public string DuplicateOf;
        public bool? Motion;
        public DateTime? ExifDate;
        public double? Lat;
        public double? Lon;
        public bool OnDrive;

        /// <summary>Dossier sur le téléphone (chemin relatif sans le nom).</summary>
        public string Folder
        {
            get
            {
                int slash = (RelativePath ?? "").LastIndexOf('/');
                return slash < 0 ? "" : RelativePath.Substring(0, slash);
            }
        }
    }

    /// <summary>Élément de galerie envoyé au site.</summary>
    internal sealed class MediaItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; }
        public long Size { get; set; }
        public DateTime? Date { get; set; }
        public string Source { get; set; }
        public bool OnDrive { get; set; }
        public bool Hidden { get; set; }
        public bool Live { get; set; }
        public string DuplicateOf { get; set; }
        public bool? Motion { get; set; }
        public DateTime? ExifDate { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }
    }
}
