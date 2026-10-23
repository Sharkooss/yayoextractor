using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using YayoPont.Core;
using YayoPont.Phone;

namespace YayoPont.Jobs
{
    /// <summary>Analyse : index de la clé, liste du téléphone, verdict « déjà sur la clé ».</summary>
    internal sealed class ScanJob
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string State { get; private set; } = "running";
        public string Phase { get; private set; } = "drive";
        public int Photos { get; private set; }
        public int Videos { get; private set; }
        public int OnDriveCount { get; private set; }
        public string Error { get; private set; }

        internal DriveDto Drive;
        internal DuplicateIndex Index;
        internal Dictionary<string, PhoneFile> Files = new Dictionary<string, PhoneFile>();
        internal List<MediaItem> Items;

        public void Run(IPhoneSource phone, DriveDto drive, CancellationToken cancel)
        {
            Drive = drive;
            try
            {
                Index = DuplicateIndex.Build(DuplicateIndex.MediaFiles(drive.Root));
                Phase = "phone";
                var files = phone.Enumerate(file =>
                {
                    if (file.Kind == "video") Videos++;
                    else Photos++;
                }, cancel);
                Phase = "compare";
                var items = new List<MediaItem>(files.Count);
                foreach (var file in files)
                {
                    cancel.ThrowIfCancellationRequested();
                    Files[file.Id] = file;
                    bool onDrive = Index.IsOnDrive(file.Name, file.Size, () => PhoneHead(phone, file.Id), DuplicateIndex.ReadHead);
                    if (onDrive) OnDriveCount++;
                    items.Add(new MediaItem
                    {
                        Id = file.Id,
                        Name = file.Name,
                        Kind = file.Kind,
                        Size = file.Size,
                        Date = file.Date,
                        Source = file.Source,
                        OnDrive = onDrive,
                    });
                }
                Items = items.OrderByDescending(i => i.Date ?? DateTime.MinValue).ToList();
                Photos = files.Count(f => f.Kind != "video");
                Videos = files.Count - Photos;
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

        private static byte[] PhoneHead(IPhoneSource phone, string id)
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

    /// <summary>Copie : jamais de suppression sur le téléphone, écriture en .part puis renommage.</summary>
    internal sealed class TransferJob
    {
        private const long Fat32Limit = 4L * 1024 * 1024 * 1024 - 1;
        private const int HeadBytes = 128 * 1024;
        private const int ErrorDiskFull = unchecked((int)0x80070070);
        private const int ErrorHandleDiskFull = unchecked((int)0x80070027);

        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private readonly object gate = new object();

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string State { get; private set; } = "running";
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

        public void Cancel() => cancel.Cancel();

        public void Run(IPhoneSource phone, DriveDto drive, IList<PhoneFile> files, DuplicateIndex index)
        {
            Total = files.Count;
            BytesTotal = files.Sum(f => f.Size);
            Folder = Destination.Root(drive.Root);
            try
            {
                var free = new DriveInfo(drive.Root).AvailableFreeSpace;
                if (BytesTotal > free)
                {
                    State = "error";
                    Error = $"Pas assez de place sur la clé : il faut {Format(BytesTotal)}, il reste {Format(free)}.";
                    return;
                }
                bool fat32 = string.Equals(drive.Fs, "FAT32", StringComparison.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    if (cancel.IsCancellationRequested) break;
                    Current = new Dictionary<string, object> { ["id"] = file.Id, ["name"] = file.Name };
                    if (fat32 && file.Size > Fat32Limit)
                    {
                        Fail(file, "Trop grosse pour cette clé (format FAT32, 4 Go maximum par fichier).");
                    }
                    else if (!CopyOne(phone, drive, file, index))
                    {
                        break; // erreur bloquante : clé pleine ou retirée, téléphone débranché
                    }
                    Done++;
                }
                if (State == "running") State = cancel.IsCancellationRequested ? "cancelled" : "done";
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
        private bool CopyOne(IPhoneSource phone, DriveDto drive, PhoneFile file, DuplicateIndex index)
        {
            string part = null;
            long counted = 0;
            try
            {
                using (var source = phone.OpenRead(file.Id))
                {
                    var head = ReadUpTo(source, HeadBytes);
                    var compareHead = head.Length <= DuplicateIndex.HeadSize ? head : head.Take(DuplicateIndex.HeadSize).ToArray();
                    if (index.IsOnDrive(file.Name, file.Size, () => compareHead, DuplicateIndex.ReadHead))
                    {
                        Skipped++;
                        Advance(file.Size);
                        return true;
                    }

                    var date = ExifDate.TryRead(head, out var exif) ? exif : file.Date ?? DateTime.Now;
                    var folder = Destination.Folder(drive.Root, date);
                    Directory.CreateDirectory(folder);
                    var name = Destination.FreeName(file.Name, n => File.Exists(Path.Combine(folder, n)));
                    var target = Path.Combine(folder, name);
                    part = target + ".part";

                    using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024))
                    {
                        output.Write(head, 0, head.Length);
                        counted += head.Length;
                        Advance(head.Length);
                        var buffer = new byte[1024 * 1024];
                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancel.IsCancellationRequested) throw new OperationCanceledException();
                            output.Write(buffer, 0, read);
                            counted += read;
                            Advance(read);
                        }
                        output.Flush(true);
                    }

                    long written = new FileInfo(part).Length;
                    if (file.Size > 0 && written != file.Size)
                        throw new IOException($"Copie incomplète ({written} octets sur {file.Size}).");
                    File.Move(part, target);
                    part = null;
                    File.SetCreationTime(target, date);
                    File.SetLastWriteTime(target, date);
                    index.Add(target, written);
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
            catch (Exception ex) when (ex is DirectoryNotFoundException || (ex is IOException && !Directory.Exists(drive.Root)))
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
}
