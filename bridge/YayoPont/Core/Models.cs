using System;

namespace YayoPont.Core
{
    /// <summary>Clé USB (ou lecteur USB) proposée comme destination.</summary>
    internal sealed class DriveDto
    {
        public string Id { get; set; }
        public string Letter { get; set; }
        public string Label { get; set; }
        public string Fs { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        internal string Root;
    }

    /// <summary>Téléphone vu par Windows : « ready » si son stockage est lisible, « locked » sinon.</summary>
    internal sealed class PhoneInfo
    {
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
    }

    /// <summary>Élément de galerie envoyé au site.</summary>
    internal sealed class MediaItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public long Size { get; set; }
        public DateTime? Date { get; set; }
        public string Source { get; set; }
        public bool OnDrive { get; set; }
    }
}
