using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using YayoPont.Core;

namespace YayoPont.Phone
{
    /// <summary>
    /// Les vrais téléphones, via Windows Portable Devices (ce qu'utilise l'Explorateur).
    /// Tous les appels COM passent par un seul thread : WPD n'aime pas les accès
    /// simultanés à un même appareil, et cela règle la question des apartments.
    /// Ouverture en lecture seule : le pont ne peut rien modifier sur le téléphone.
    /// Plusieurs appareils peuvent être ouverts ; les lectures visent le téléphone
    /// de la dernière analyse.
    /// </summary>
    internal sealed class WpdPhoneSource : IPhoneSource
    {
        private const int ErrorNotConnected = unchecked((int)0x8007048F);

        private sealed class Opened
        {
            public IPortableDevice Device;
            public PhoneInfo Info;
        }

        /// <summary>Ce qu'on retient d'un objet du téléphone avant de classer son dossier.</summary>
        private struct Child
        {
            public string Id;
            public string Name;
            public bool Folder;
            public long Size;
            public DateTime? Date;
        }

        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Thread worker;
        private readonly Dictionary<string, PhoneFile> known = new Dictionary<string, PhoneFile>();

        // Uniquement sur le thread WPD.
        private readonly Dictionary<string, Opened> opened = new Dictionary<string, Opened>(StringComparer.Ordinal);
        // Appareils WPD qui ne sont pas des téléphones (clés USB en « MSC ») : pas rouverts à chaque sondage.
        private readonly HashSet<string> notPhones = new HashSet<string>(StringComparer.Ordinal);
        private List<string> order = new List<string>();
        private IPortableDeviceManager manager;
        private string currentId;

        public WpdPhoneSource()
        {
            worker = new Thread(() =>
            {
                foreach (var work in queue.GetConsumingEnumerable()) work();
            })
            { IsBackground = true, Name = "wpd" };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
        }

        public IList<PhoneInfo> Phones() => Run(PhonesOnWorker);

        public IList<PhoneFile> Enumerate(string phoneId, Action<PhoneFile> found, CancellationToken cancel) =>
            Run(() => EnumerateOnWorker(phoneId, found, cancel));

        public Stream OpenRead(string id) => Run(() =>
        {
            var stream = OpenResource(id, Wpd.ResourceDefault, out uint bufferSize);
            if (stream == null) throw new IOException("Impossible de lire ce fichier sur le téléphone.");
            return (Stream)new WpdStream(this, stream, bufferSize);
        });

        public byte[] Thumbnail(string id)
        {
            var thumb = Run(() =>
            {
                var stream = OpenResource(id, Wpd.ResourceThumbnail, out uint bufferSize);
                return stream == null ? null : ReadAll(stream, bufferSize, 4 * 1024 * 1024);
            });
            if (thumb != null && thumb.Length > 0) return Thumbnails.Normalize(thumb);
            PhoneFile file;
            lock (known) known.TryGetValue(id, out file);
            if (file == null || !Thumbnails.CanDecode(file.Name) || file.Size > 15 * 1024 * 1024) return null;
            using (var full = OpenRead(id)) return Thumbnails.FromStream(full, file.Name);
        }

        public bool UsbSeen() => PhoneUsb.Seen();

        public void Dispose()
        {
            Run<object>(() =>
            {
                foreach (var id in opened.Keys.ToList()) Close(id);
                return null;
            });
            queue.CompleteAdding();
        }

        // ---------- exécuté sur le thread WPD ----------

        private IPortableDevice Current => currentId != null && opened.TryGetValue(currentId, out var o) ? o.Device : null;

        private IList<PhoneInfo> PhonesOnWorker()
        {
            Trace("detect: manager");
            if (manager == null) manager = Wpd.Create<IPortableDeviceManager>(Wpd.ClsidManager);
            manager.RefreshDeviceList();
            uint count = 0;
            manager.GetDevices(null, ref count);
            Trace("detect: " + count + " appareil(s)");
            var ids = new List<string>();
            if (count > 0)
            {
                var pointers = new IntPtr[count];
                if (manager.GetDevices(pointers, ref count) >= 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        ids.Add(Marshal.PtrToStringUni(pointers[i]));
                        Marshal.FreeCoTaskMem(pointers[i]);
                    }
                }
            }

            foreach (var gone in opened.Keys.Where(id => !ids.Contains(id)).ToList()) Close(gone);
            notPhones.IntersectWith(ids);

            var phones = new List<PhoneInfo>();
            foreach (var id in ids)
            {
                if (notPhones.Contains(id)) continue;
                if (opened.TryGetValue(id, out var already))
                {
                    try
                    {
                        already.Info.State = HasStorage(already.Device) ? "ready" : "locked";
                        phones.Add(already.Info);
                        continue;
                    }
                    catch (IOException)
                    {
                        Close(id); // débranché entre deux sondages : on repart de zéro
                    }
                }
                try
                {
                    var info = TryOpen(id);
                    if (info != null) phones.Add(info);
                }
                catch (COMException ex)
                {
                    Log.Error("ouverture de l'appareil " + id, ex);
                    Close(id);
                }
            }
            order = phones.Select(p => p.Id).ToList();
            if (currentId != null && !opened.ContainsKey(currentId)) currentId = null;
            return phones;
        }

        private PhoneInfo TryOpen(string id)
        {
            Trace("open: " + id);
            var candidate = Wpd.Create<IPortableDevice>(Wpd.ClsidDeviceFtm);
            Trace("open: créé");
            if (candidate.Open(id, ClientInfo()) < 0) { Release(candidate); return null; }
            Trace("open: ouvert");
            if (!candidate.Content(out var content).Ok() || !content.Properties(out var props).Ok())
            {
                CloseQuietly(candidate);
                return null;
            }
            Trace("open: contenu et propriétés");
            var values = Values(props, Wpd.DeviceObjectId, Keys(Wpd.DeviceProtocol, Wpd.DeviceFriendlyName, Wpd.DeviceManufacturer, Wpd.DeviceModel));
            Trace("open: valeurs " + (values != null));
            var protocol = String(values, Wpd.DeviceProtocol) ?? "";
            Trace("open: protocole " + protocol);
            // Les clés USB apparaissent aussi dans WPD (protocole « MSC ») : on les écarte pour de bon.
            if (protocol.StartsWith("MSC", StringComparison.OrdinalIgnoreCase))
            {
                CloseQuietly(candidate);
                notPhones.Add(id);
                return null;
            }
            var name = String(values, Wpd.DeviceFriendlyName);
            if (string.IsNullOrWhiteSpace(name)) name = String(values, Wpd.DeviceModel) ?? FriendlyName(id) ?? "Téléphone";
            var info = new PhoneInfo { Id = id, Name = name, Manufacturer = String(values, Wpd.DeviceManufacturer) ?? "" };
            opened[id] = new Opened { Device = candidate, Info = info };
            Trace("open: stockage ?");
            info.State = HasStorage(candidate) ? "ready" : "locked";
            Log.Info($"téléphone détecté : {info.Manufacturer} {name} ({protocol})");
            return info;
        }

        /// <summary>
        /// Un Android verrouillé ou qui n'a pas encore « autorisé l'accès aux
        /// données » se montre, mais sans stockage.
        /// </summary>
        private bool HasStorage(IPortableDevice device)
        {
            try { return Children(device, Wpd.DeviceObjectId).Count > 0; }
            catch (COMException) { return false; }
        }

        /// <summary>Le téléphone des lectures : celui de la dernière analyse, sinon le premier branché.</summary>
        private IPortableDevice CurrentOrFirst()
        {
            if (Current != null) return Current;
            PhonesOnWorker();
            currentId = order.FirstOrDefault(opened.ContainsKey);
            return Current;
        }

        private IList<PhoneFile> EnumerateOnWorker(string phoneId, Action<PhoneFile> found, CancellationToken cancel)
        {
            if (phoneId == null || !opened.ContainsKey(phoneId)) PhonesOnWorker();
            var chosen = phoneId ?? order.FirstOrDefault(opened.ContainsKey);
            if (chosen == null) throw new IOException("Aucun téléphone branché.");
            if (!opened.ContainsKey(chosen)) throw new IOException("Ce téléphone n'est plus branché.");
            currentId = chosen;
            var device = Current;
            Check(device.Content(out var content));
            Check(content.Properties(out var props));
            var keys = Keys(Wpd.ObjectOriginalFileName, Wpd.ObjectName, Wpd.ObjectContentType,
                Wpd.ObjectSize, Wpd.ObjectDateCreated, Wpd.ObjectDateModified);
            var files = new List<PhoneFile>();
            foreach (var storage in Children(device, Wpd.DeviceObjectId))
            {
                Walk(storage, "", 0, false);
            }
            lock (known)
            {
                known.Clear();
                foreach (var file in files) known[file.Id] = file;
            }
            return files;

            void Walk(string parent, string relative, int depth, bool parentHidden)
            {
                // Tous les enfants d'abord : le « .nomedia » peut venir après les photos.
                var children = new List<Child>();
                foreach (var id in Children(device, parent))
                {
                    cancel.ThrowIfCancellationRequested();
                    var values = Values(props, id, keys);
                    if (values == null) continue;
                    var type = Guid(values, Wpd.ObjectContentType);
                    children.Add(new Child
                    {
                        Id = id,
                        Name = String(values, Wpd.ObjectOriginalFileName) ?? String(values, Wpd.ObjectName) ?? id,
                        Folder = type == Wpd.ContentTypeFolder || type == Wpd.ContentTypeFunctionalObject,
                        Size = (long)ULong(values, Wpd.ObjectSize),
                        Date = Date(values, Wpd.ObjectDateCreated) ?? Date(values, Wpd.ObjectDateModified),
                    });
                    Release(values);
                }
                bool hidden = GalleryRules.FolderHidden(parentHidden, relative, children.Where(c => !c.Folder).Select(c => c.Name));
                foreach (var child in children)
                {
                    if (child.Folder)
                    {
                        if (depth >= MediaRules.MaxDepth || MediaRules.SkipFolder(child.Name, relative)) continue;
                        Walk(child.Id, relative.Length == 0 ? child.Name : relative + "/" + child.Name, depth + 1, hidden);
                        continue;
                    }
                    var kind = MediaRules.Kind(child.Name);
                    if (kind == null) continue;
                    var rel = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
                    files.Add(new PhoneFile
                    {
                        Id = child.Id,
                        Name = child.Name,
                        RelativePath = rel,
                        Size = child.Size,
                        Date = child.Date,
                        Kind = kind,
                        Source = MediaRules.Source(rel),
                        Hidden = hidden || MediaRules.HiddenFile(child.Name),
                    });
                    found(files[files.Count - 1]);
                }
            }
        }

        private List<string> Children(IPortableDevice device, string parent)
        {
            Check(device.Content(out var content));
            var ids = new List<string>();
            if (content.EnumObjects(0, parent, null, out var objects) < 0) return ids;
            var batch = new IntPtr[64];
            while (true)
            {
                uint fetched = 0;
                int hr = objects.Next((uint)batch.Length, batch, ref fetched);
                for (int i = 0; i < fetched; i++)
                {
                    ids.Add(Marshal.PtrToStringUni(batch[i]));
                    Marshal.FreeCoTaskMem(batch[i]);
                }
                if (hr != 0 || fetched == 0) break; // S_FALSE : fin de liste
            }
            Release(objects);
            return ids;
        }

        private IStream OpenResource(string id, PropertyKey resource, out uint bufferSize)
        {
            bufferSize = 0;
            var device = CurrentOrFirst();
            if (device == null) throw new IOException("Aucun téléphone branché.");
            Check(device.Content(out var content));
            Check(content.Transfer(out var resources));
            var key = resource;
            int hr = resources.GetStream(id, ref key, Wpd.StgmRead, ref bufferSize, out var stream);
            if (hr == ErrorNotConnected) throw new IOException("Le téléphone a été débranché.");
            return hr < 0 ? null : stream;
        }

        internal int ReadOnWorker(IStream stream, byte[] buffer, int count) => Run(() =>
        {
            var readPtr = Marshal.AllocCoTaskMem(4);
            try
            {
                stream.Read(buffer, count, readPtr);
                return Marshal.ReadInt32(readPtr);
            }
            catch (COMException ex) when (ex.ErrorCode == ErrorNotConnected)
            {
                throw new IOException("Le téléphone a été débranché.", ex);
            }
            finally
            {
                Marshal.FreeCoTaskMem(readPtr);
            }
        });

        internal void ReleaseOnWorker(object comObject) => Run<object>(() => { Release(comObject); return null; });

        private static byte[] ReadAll(IStream stream, uint bufferSize, int max)
        {
            var output = new MemoryStream();
            var buffer = new byte[Math.Max(4096, (int)Math.Min(bufferSize, 1024 * 1024))];
            var readPtr = Marshal.AllocCoTaskMem(4);
            try
            {
                while (output.Length < max)
                {
                    stream.Read(buffer, buffer.Length, readPtr);
                    int read = Marshal.ReadInt32(readPtr);
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                }
            }
            catch (COMException) { return null; }
            finally
            {
                Marshal.FreeCoTaskMem(readPtr);
                Release(stream);
            }
            return output.ToArray();
        }

        private void Close(string id)
        {
            if (opened.TryGetValue(id, out var o)) CloseQuietly(o.Device);
            opened.Remove(id);
            if (currentId == id) currentId = null;
        }

        private static void CloseQuietly(IPortableDevice device)
        {
            if (device == null) return;
            try { device.Close(); } catch (COMException) { }
            Release(device);
        }

        private string FriendlyName(string id)
        {
            uint length = 0;
            manager.GetDeviceFriendlyName(id, IntPtr.Zero, ref length);
            if (length == 0) return null;
            var buffer = Marshal.AllocCoTaskMem((int)length * 2);
            try
            {
                return manager.GetDeviceFriendlyName(id, buffer, ref length) < 0 ? null : Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        private static IPortableDeviceValues ClientInfo()
        {
            var info = Wpd.Create<IPortableDeviceValues>(Wpd.ClsidValues);
            var k = Wpd.ClientName; info.SetStringValue(ref k, "Pont Yayo");
            k = Wpd.ClientMajorVersion; info.SetUnsignedIntegerValue(ref k, 1);
            k = Wpd.ClientMinorVersion; info.SetUnsignedIntegerValue(ref k, 1);
            k = Wpd.ClientRevision; info.SetUnsignedIntegerValue(ref k, 0);
            k = Wpd.ClientSecurityQos; info.SetUnsignedIntegerValue(ref k, Wpd.SecurityImpersonation);
            k = Wpd.ClientDesiredAccess; info.SetUnsignedIntegerValue(ref k, Wpd.GenericRead);
            return info;
        }

        /// <summary>Collection de clés de propriétés (seulement lue par WPD : réutilisable).</summary>
        private static IPortableDeviceKeyCollection Keys(params PropertyKey[] keys)
        {
            var collection = Wpd.Create<IPortableDeviceKeyCollection>(Wpd.ClsidKeyCollection);
            foreach (var key in keys)
            {
                var k = key;
                collection.Add(ref k);
            }
            return collection;
        }

        private static IPortableDeviceValues Values(IPortableDeviceProperties props, string objectId, IPortableDeviceKeyCollection keys) =>
            props.GetValues(objectId, keys, out var values) < 0 ? null : values;

        private static string String(IPortableDeviceValues values, PropertyKey key)
        {
            if (values == null) return null;
            return values.GetStringValue(ref key, out var value) < 0 || string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static Guid Guid(IPortableDeviceValues values, PropertyKey key) =>
            values.GetGuidValue(ref key, out var value) < 0 ? System.Guid.Empty : value;

        private static ulong ULong(IPortableDeviceValues values, PropertyKey key) =>
            values.GetUnsignedLargeIntegerValue(ref key, out var value) < 0 ? 0 : value;

        private static DateTime? Date(IPortableDeviceValues values, PropertyKey key)
        {
            if (values.GetValue(ref key, out var variant) < 0) return null;
            try
            {
                if (variant.Type != PropVariant.VtDate || variant.Date <= 0) return null;
                var date = DateTime.FromOADate(variant.Date);
                return date.Year >= 1990 ? date : (DateTime?)null;
            }
            finally
            {
                PropVariant.PropVariantClear(ref variant);
            }
        }

        /// <summary>Fil d'Ariane dans le journal, seulement avec YAYO_TRACE=1 : survit à un plantage natif.</summary>
        private static readonly bool Tracing = Environment.GetEnvironmentVariable("YAYO_TRACE") == "1";

        private static void Trace(string step)
        {
            if (Tracing) Log.Info("wpd " + step);
        }

        private static void Check(int hr)
        {
            if (hr == ErrorNotConnected) throw new IOException("Le téléphone a été débranché.");
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        private static void Release(object comObject)
        {
            if (comObject != null && Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }

        private T Run<T>(Func<T> work)
        {
            if (Thread.CurrentThread == worker) return work();
            T result = default;
            Exception error = null;
            using (var done = new ManualResetEventSlim())
            {
                queue.Add(() =>
                {
                    try { result = work(); }
                    catch (Exception ex) { error = ex; }
                    finally { done.Set(); }
                });
                done.Wait();
            }
            if (error is COMException com && com.ErrorCode == ErrorNotConnected) throw new IOException("Le téléphone a été débranché.", com);
            if (error != null) throw new IOException(error.Message, error);
            return result;
        }
    }

    internal static class HResult
    {
        public static bool Ok(this int hr) => hr >= 0;
    }

    /// <summary>Flux d'un fichier du téléphone ; chaque lecture passe par le thread WPD.</summary>
    internal sealed class WpdStream : Stream
    {
        private readonly WpdPhoneSource owner;
        private readonly IStream stream;
        private bool disposed;

        public WpdStream(WpdPhoneSource owner, IStream stream, uint bufferSize)
        {
            this.owner = owner;
            this.stream = stream;
            OptimalBufferSize = bufferSize > 0 ? (int)Math.Min(bufferSize, 4 * 1024 * 1024) : 256 * 1024;
        }

        public int OptimalBufferSize { get; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (offset == 0) return owner.ReadOnWorker(stream, buffer, count);
            var temp = new byte[count];
            int read = owner.ReadOnWorker(stream, temp, count);
            Array.Copy(temp, 0, buffer, offset, read);
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposed) owner.ReleaseOnWorker(stream);
            disposed = true;
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
