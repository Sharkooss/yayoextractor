using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using YayoPont.Core;
using YayoPont.Phone;

namespace YayoPont.Jobs
{
    internal sealed class ScanOptions
    {
        /// <summary>Analyse approfondie : lit la tête de chaque photo (date EXIF, GPS) et des vidéos (GPS).</summary>
        public bool Deep;
    }

    /// <summary>
    /// Analyse : index de la destination, liste du téléphone, ce que la Galerie
    /// ne montre pas (masqués, clips de photos animées, doublons du téléphone),
    /// verdict « déjà sur la clé », et en analyse approfondie EXIF et GPS.
    /// </summary>
    internal sealed class ScanJob
    {
        /// <summary>Un clip de photo animée fait quelques secondes : au-delà, c'est une vraie vidéo.</summary>
        public const long LiveMaxBytes = 12L * 1024 * 1024;
        private const int DetailsPhotoHead = 256 * 1024;
        private const int DetailsVideoHead = 512 * 1024;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string State { get; private set; } = "running";
        public string Phase { get; private set; } = "drive";
        public int Photos { get; private set; }
        public int Videos { get; private set; }
        public int OnDriveCount { get; private set; }
        public int Hidden { get; private set; }
        public int Live { get; private set; }
        public int Duplicates { get; private set; }
        public int DetailsDone { get; private set; }
        public int DetailsTotal { get; private set; }
        public string Error { get; private set; }

        internal Target Target;
        internal string PhoneId;
        internal DuplicateIndex Index;
        internal Dictionary<string, PhoneFile> Files = new Dictionary<string, PhoneFile>();
        internal List<MediaItem> Items;

        public void Run(IPhoneSource phone, Target target, CancellationToken cancel) =>
            Run(phone, null, target, new ScanOptions(), cancel);

        public void Run(IPhoneSource phone, string phoneId, Target target, ScanOptions options, CancellationToken cancel)
        {
            Target = target;
            PhoneId = phoneId;
            try
            {
                Index = DuplicateIndex.ForTarget(target);
                Phase = "phone";
                var files = phone.Enumerate(phoneId, file =>
                {
                    if (file.Kind == "video") Videos++;
                    else Photos++;
                    if (file.Hidden) Hidden++;
                }, cancel);
                Phase = "compare";
                foreach (var file in files) Files[file.Id] = file;
                MarkLive(files);
                Live = files.Count(f => f.Live);
                MarkDuplicates(phone, files, cancel);
                Duplicates = files.Count(f => f.DuplicateOf != null);
                foreach (var file in files)
                {
                    cancel.ThrowIfCancellationRequested();
                    file.OnDrive = Index.IsOnDrive(file.Name, file.Size, () => PhoneHead(phone, file.Id), DuplicateIndex.ReadHead);
                    if (file.OnDrive) OnDriveCount++;
                }
                if (options != null && options.Deep)
                {
                    Phase = "details";
                    ReadDetails(phone, files, cancel);
                }
                Items = files.Select(ToItem).OrderByDescending(i => i.Date ?? DateTime.MinValue).ToList();
                Photos = files.Count(f => f.Kind != "video");
                Videos = files.Count - Photos;
                Hidden = files.Count(f => f.Hidden);
                State = "done";
            }
            catch (OperationCanceledException)
            {
                State = "error";
                Error = "Analyse annulée.";
            }
            catch (Exception ex)
            {
                Log.Error("analyse", ex);
                State = "error";
                Error = ex is IOException ? ex.Message : "L'analyse du téléphone a échoué.";
            }
        }

        /// <summary>
        /// Clip compagnon d'une photo animée (IMG_1234.HEIC + IMG_1234.MOV) : même
        /// dossier, même nom qu'une photo, 12 Mo au plus. La durée n'est pas lisible
        /// sans lire le fichier : elle n'est vérifiée que sur la destination.
        /// </summary>
        internal static void MarkLive(IList<PhoneFile> files)
        {
            var photos = new HashSet<string>(files.Where(f => f.Kind == "photo").Select(StemKey), StringComparer.OrdinalIgnoreCase);
            foreach (var video in files.Where(f => f.Kind == "video"))
                video.Live = video.Size <= LiveMaxBytes && photos.Contains(StemKey(video));
        }

        private static string StemKey(PhoneFile f) => f.Folder + "/" + Path.GetFileNameWithoutExtension(f.Name);

        /// <summary>
        /// Doublons internes au téléphone : même taille, puis mêmes 64 premiers Ko
        /// (seuls les fichiers de même taille sont lus). On garde un élément visible
        /// dans la Galerie, de l'appareil photo, sans « (n) » dans le nom, sinon le premier.
        /// </summary>
        internal static void MarkDuplicates(IPhoneSource phone, IList<PhoneFile> files, CancellationToken cancel)
        {
            var position = new Dictionary<PhoneFile, int>();
            for (int i = 0; i < files.Count; i++) position[files[i]] = i;
            using (var sha = SHA256.Create())
            {
                foreach (var sameSize in files.Where(f => f.Size > 0).GroupBy(f => f.Size).Where(g => g.Count() > 1))
                {
                    var byHead = new Dictionary<string, List<PhoneFile>>();
                    foreach (var file in sameSize)
                    {
                        cancel.ThrowIfCancellationRequested();
                        var head = PhoneHead(phone, file.Id);
                        if (head == null) continue;
                        var key = Convert.ToBase64String(sha.ComputeHash(head));
                        if (!byHead.TryGetValue(key, out var list)) byHead[key] = list = new List<PhoneFile>();
                        list.Add(file);
                    }
                    foreach (var group in byHead.Values.Where(l => l.Count > 1))
                    {
                        var keep = group
                            .OrderBy(f => f.Hidden)
                            .ThenBy(f => f.Source == "camera" ? 0 : 1)
                            .ThenBy(f => Destination.HasNumber(f.Name))
                            .ThenBy(f => position[f])
                            .First();
                        foreach (var other in group.Where(f => f != keep)) other.DuplicateOf = keep.Id;
                    }
                }
            }
        }

        private void ReadDetails(IPhoneSource phone, IList<PhoneFile> files, CancellationToken cancel)
        {
            var wanted = files.Where(f => ExifPhoto(f.Name) || Mp4Info.IsMp4Family(f.Name)).ToList();
            DetailsTotal = wanted.Count;
            foreach (var file in wanted)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    using (var stream = phone.OpenRead(file.Id))
                    {
                        if (file.Kind == "photo")
                        {
                            var head = ExifInfo.ReadHead(stream, DetailsPhotoHead);
                            var exif = ExifInfo.Read(head);
                            file.ExifDate = exif.Date;
                            file.Lat = exif.Lat;
                            file.Lon = exif.Lon;
                            if (MotionPhoto.IsJpegName(file.Name) && MotionPhoto.HeadSaysMotion(head)) file.Motion = true;
                        }
                        else if (Mp4Info.TryLocation(Mp4Info.ReadMoovFromHead(stream, DetailsVideoHead), out var lat, out var lon))
                        {
                            file.Lat = lat;
                            file.Lon = lon;
                        }
                    }
                }
                catch (IOException ex) when (!ex.Message.Contains("débranché"))
                {
                    Log.Error("analyse approfondie de " + file.RelativePath, ex);
                }
                DetailsDone++;
            }
        }

        private static bool ExifPhoto(string name)
        {
            var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            return ext == ".jpg" || ext == ".jpeg" || ext == ".heic" || ext == ".heif";
        }

        private static MediaItem ToItem(PhoneFile f) => new MediaItem
        {
            Id = f.Id,
            Name = f.Name,
            Path = f.RelativePath,
            Kind = f.Kind,
            Size = f.Size,
            Date = f.Date,
            Source = f.Source,
            OnDrive = f.OnDrive,
            Hidden = f.Hidden,
            Live = f.Live,
            DuplicateOf = f.DuplicateOf,
            Motion = f.Motion,
            ExifDate = f.ExifDate,
            Lat = f.Lat,
            Lon = f.Lon,
        };

        internal static byte[] PhoneHead(IPhoneSource phone, string id)
        {
            try
            {
                using (var stream = phone.OpenRead(id)) return DuplicateIndex.ReadHead(stream);
            }
            catch (IOException) { return null; }
        }
    }

    internal sealed class TransferFailure
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Error { get; set; }
    }

    internal sealed class CopiedFile
    {
        public string Id { get; set; }
        public string Name { get; set; }

        /// <summary>Chemin relatif à la destination, séparateur « / ».</summary>
        public string Path { get; set; }
    }

    /// <summary>Options de copie ; absentes, c'est le comportement du mode normal.</summary>
    internal sealed class TransferOptions
    {
        public string Layout = Core.Layout.DefaultFolder;
        public string FileName = Core.Layout.DefaultFileName;
        public IDictionary<string, object> Places;
        public bool StripMotion = true;
        public bool Repair = true;
        public bool Dedupe = true;
        public string Conflict = "rename";
        public bool Verify;
        public bool KeepDates = true;

        internal LayoutValues Values(PhoneFile file, DateTime? date) => new LayoutValues
        {
            Date = date,
            Source = file.Source,
            Kind = file.Kind,
            Name = file.Name,
            Place = Places != null && Places.TryGetValue(file.Id, out var place) ? place as IDictionary<string, object> : null,
        };
    }

    /// <summary>
    /// Copie : jamais de suppression sur le téléphone, écriture en .part puis
    /// renommage. Avant de copier, « Ranger la clé » dans le dossier géré.
    /// </summary>
    internal sealed class TransferJob
    {
        private const long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;
        private const int HeadBytes = 256 * 1024;
        private const long MaxInMemory = 200L * 1024 * 1024;
        private const int ErrorDiskFull = unchecked((int)0x80070070);
        private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private readonly object gate = new object();
        private readonly List<CopiedFile> copiedFiles = new List<CopiedFile>();

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string State { get; private set; } = "running";
        public string Phase { get; private set; } = "repair";
        public int Total { get; private set; }
        public int Done { get; private set; }
        public long BytesTotal { get; private set; }
        public long BytesDone { get; private set; }
        public int Copied { get; private set; }
        public int Skipped { get; private set; }
        public string Error { get; private set; }
        public string Folder { get; private set; }
        public Dictionary<string, object> Current { get; private set; }
        public List<TransferFailure> Failed { get; } = new List<TransferFailure>();
        public RepairReport Repair { get; private set; } = new RepairReport();

        /// <summary>Les fichiers écrits, donnés à la fin seulement (14 000 lignes à chaque sondage seraient du gâchis).</summary>
        public List<CopiedFile> CopiedFiles
        {
            get
            {
                if (State == "running") return null;
                lock (gate) return copiedFiles.ToList();
            }
        }

        public void Cancel() => cancel.Cancel();

        public void Run(IPhoneSource phone, Target target, IList<PhoneFile> files, DuplicateIndex index) =>
            Run(phone, target, files, index, new TransferOptions());

        /// <param name="index">index des doublons de l'analyse, ou null pour le refaire.</param>
        public void Run(IPhoneSource phone, Target target, IList<PhoneFile> files, DuplicateIndex index, TransferOptions options)
        {
            options = options ?? new TransferOptions();
            Total = files.Count;
            BytesTotal = files.Sum(f => f.Size);
            Folder = target.ManagedRoot;
            try
            {
                if (options.Repair)
                {
                    Phase = "repair";
                    var names = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    // Clips choisis exprès (mode avancé) : ne pas les retirer pour les recopier aussitôt.
                    bool removeLive = !files.Any(f => f.Live);
                    Repair = KeyRepair.Run(target.ManagedRoot, options.StripMotion, removeLive, names, cancel.Token, target.RootName.Length > 0);
                    if (Repair.Changed) index = null;
                }
                if (index == null) index = DuplicateIndex.ForTarget(target);
                Phase = "copy";

                var free = new DriveInfo(Path.GetPathRoot(target.Root)).AvailableFreeSpace;
                if (BytesTotal > free)
                {
                    State = "error";
                    Error = $"Pas assez de place sur la clé : il faut {Format(BytesTotal)}, il reste {Format(free)}.";
                    return;
                }
                bool fat32 = string.Equals(target.Fs, "FAT32", StringComparison.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (cancel.IsCancellationRequested) break;
                    Current = new Dictionary<string, object> { ["id"] = file.Id, ["name"] = file.Name };
                    if (fat32 && file.Size > Fat32Limit)
                    {
                        Fail(file, "Trop grosse pour cette clé (format FAT32, 4 Go maximum par fichier).");
                    }
                    else if (!CopyOne(phone, target, file, index, options))
                    {
                        break; // erreur bloquante : clé pleine ou retirée, téléphone débranché
                    }
                    Done++;
                }
                if (State == "running") State = cancel.IsCancellationRequested ? "cancelled" : "done";
            }
            catch (OperationCanceledException)
            {
                State = "cancelled";
            }
            catch (Exception ex)
            {
                Log.Error("transfert", ex);
                State = "error";
                Error = "Le transfert a échoué de façon inattendue.";
            }
            finally
            {
                Current = null;
            }
        }

        /// <summary>false si le transfert doit s'arrêter.</summary>
        private bool CopyOne(IPhoneSource phone, Target target, PhoneFile file, DuplicateIndex index, TransferOptions options)
        {
            string part = null;
            long counted = 0;
            var root = target.ManagedRoot;
            try
            {
                using (var source = phone.OpenRead(file.Id))
                {
                    var head = ReadUpTo(source, HeadBytes);
                    var compareHead = head.Length <= DuplicateIndex.HeadSize ? head : head.Take(DuplicateIndex.HeadSize).ToArray();
                    if (options.Dedupe && index.IsOnDrive(file.Name, file.Size, () => compareHead, DuplicateIndex.ReadHead))
                    {
                        Skipped++;
                        Advance(file.Size);
                        return true;
                    }
                    counted = head.Length;
                    Advance(head.Length);

                    // Photo : lue entièrement en mémoire pour en retirer la vidéo d'une photo animée.
                    byte[] whole = null;
                    int length = 0;
                    if (options.StripMotion && MotionPhoto.IsJpegName(file.Name) && file.Size <= MaxInMemory)
                    {
                        var memory = new MemoryStream((int)Math.Max(file.Size, head.Length));
                        memory.Write(head, 0, head.Length);
                        counted += CopyRest(source, memory, null);
                        CheckRead(file, counted);
                        whole = memory.GetBuffer();
                        length = MotionPhoto.Strip(whole, (int)memory.Length);
                    }

                    var exif = ExifInfo.Read(head);
                    DateTime? date = exif.Date ?? file.ExifDate ?? file.Date;
                    var values = options.Values(file, date);
                    var folder = Path.Combine(root, Layout.Folder(options.Layout, values));
                    if (!Target.Inside(Path.GetFullPath(folder), Path.GetFullPath(root)))
                        throw new IOException("Chemin de rangement refusé.");
                    Directory.CreateDirectory(folder);
                    var name = Layout.FileName(options.FileName, values);
                    var targetPath = Path.Combine(folder, name);
                    bool replace = false;
                    if (Exists(targetPath))
                    {
                        if (options.Conflict == "skip")
                        {
                            Skipped++;
                            Advance(file.Size - counted);
                            return true;
                        }
                        if (options.Conflict == "overwrite" && File.Exists(targetPath)) replace = true;
                        else
                        {
                            name = Destination.FreeName(name, n => Exists(Path.Combine(folder, n)));
                            targetPath = Path.Combine(folder, name);
                        }
                    }
                    part = targetPath + ".part";

                    long written;
                    using (var sha = options.Verify ? SHA256.Create() : null)
                    {
                        using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                        {
                            if (whole != null)
                            {
                                output.Write(whole, 0, length);
                                sha?.TransformBlock(whole, 0, length, null, 0);
                            }
                            else
                            {
                                output.Write(head, 0, head.Length);
                                sha?.TransformBlock(head, 0, head.Length, null, 0);
                                counted += CopyRest(source, output, sha);
                            }
                            output.Flush(true);
                        }
                        if (whole == null) CheckRead(file, counted);
                        written = new FileInfo(part).Length;
                        long expected = whole != null ? length : counted;
                        if (written != expected) throw new IOException($"Copie incomplète ({written} octets écrits sur {expected}).");
                        if (Mp4Info.IsMp4Family(name) && Mp4Info.IsComplete(part) == false)
                            throw new IOException("Vidéo incomplète : rebranche le téléphone et relance la copie.");
                        if (sha != null)
                        {
                            sha.TransformFinalBlock(new byte[0], 0, 0);
                            if (KeyRepair.Sha256(part) != Convert.ToBase64String(sha.Hash))
                                throw new IOException("La vérification a échoué : le fichier relu sur la destination est différent.");
                        }
                    }

                    if (replace)
                    {
                        File.SetAttributes(targetPath, FileAttributes.Normal);
                        File.Delete(targetPath);
                    }
                    File.Move(part, targetPath);
                    part = null;
                    if (options.KeepDates && date.HasValue)
                    {
                        File.SetCreationTime(targetPath, date.Value);
                        File.SetLastWriteTime(targetPath, date.Value);
                    }
                    index.Add(targetPath, written);
                    index.AddCopy(file.Name, file.Size);
                    CopyIndex.Append(root, file.Name, file.Size, targetPath, written);
                    lock (gate) copiedFiles.Add(new CopiedFile { Id = file.Id, Name = file.Name, Path = target.Relative(targetPath) });
                    Copied++;
                    Advance(file.Size - counted); // la taille annoncée peut différer légèrement
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException ex) when (ex.Message.Contains("débranché"))
            {
                State = "error";
                Error = "Le téléphone a été débranché. Rebranche-le puis relance : ce qui est déjà copié ne sera pas recopié.";
                return false;
            }
            catch (IOException ex) when (ex.HResult == ErrorDiskFull || ex.HResult == ErrorHandleDiskFull)
            {
                State = "error";
                Error = "La clé est pleine.";
                return false;
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException || (ex is IOException && !Directory.Exists(target.Root)))
            {
                State = "error";
                Error = "La clé a été retirée pendant la copie.";
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("copie de " + file.RelativePath, ex);
                Fail(file, ex is IOException ? ex.Message : "Impossible de copier ce fichier.");
                Advance(file.Size - counted);
                return true;
            }
            finally
            {
                if (part != null) TryDelete(part);
            }
        }

        /// <summary>Le reste du flux du téléphone vers <paramref name="output"/> ; renvoie le nombre d'octets lus.</summary>
        private long CopyRest(Stream source, Stream output, HashAlgorithm sha)
        {
            long total = 0;
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (cancel.IsCancellationRequested) throw new OperationCanceledException();
                output.Write(buffer, 0, read);
                sha?.TransformBlock(buffer, 0, read, null, 0);
                total += read;
                Advance(read);
            }
            return total;
        }

        /// <summary>On vérifie ce qui a été LU sur le téléphone : l'écrit peut être plus court (vidéo retirée).</summary>
        private static void CheckRead(PhoneFile file, long read)
        {
            if (file.Size > 0 && read != file.Size)
                throw new IOException($"Copie incomplète ({read} octets lus sur {file.Size}).");
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        private void Fail(PhoneFile file, string error)
        {
            lock (gate) Failed.Add(new TransferFailure { Id = file.Id, Name = file.Name, Error = error });
        }

        private void Advance(long bytes)
        {
            if (bytes > 0) BytesDone = Math.Min(BytesTotal, BytesDone + bytes);
        }

        private static byte[] ReadUpTo(Stream stream, int max)
        {
            var buffer = new byte[max];
            int total = 0, read;
            while (total < max && (read = stream.Read(buffer, total, max - total)) > 0) total += read;
            if (total == max) return buffer;
            var head = new byte[total];
            Array.Copy(buffer, head, total);
            return head;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        internal static string Format(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " Go";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0") + " Mo";
            return Math.Max(1, bytes / 1024) + " Ko";
        }
    }

    /// <summary>Aperçu du rangement : rien n'est lu sur le téléphone ni écrit sur la destination.</summary>
    internal static class TransferPlan
    {
        public static Dictionary<string, object> Build(Target target, IList<PhoneFile> files, TransferOptions options)
        {
            var folders = new SortedDictionary<string, (int count, long bytes)>(StringComparer.OrdinalIgnoreCase);
            int toCopy = 0, skipped = 0;
            long bytes = 0;
            foreach (var file in files)
            {
                if (options.Dedupe && file.OnDrive)
                {
                    skipped++;
                    continue;
                }
                var values = options.Values(file, file.ExifDate ?? file.Date);
                var folder = target.Relative(Path.Combine(target.ManagedRoot, Layout.Folder(options.Layout, values)).TrimEnd('\\') + "\\").TrimEnd('/');
                folders.TryGetValue(folder, out var current);
                folders[folder] = (current.count + 1, current.bytes + file.Size);
                toCopy++;
                bytes += file.Size;
            }
            return new Dictionary<string, object>
            {
                ["folders"] = folders.Select(f => new Dictionary<string, object> { ["path"] = f.Key, ["count"] = f.Value.count, ["bytes"] = f.Value.bytes }).ToList(),
                ["toCopy"] = toCopy,
                ["skipped"] = skipped,
                ["bytes"] = bytes,
            };
        }
    }
}
