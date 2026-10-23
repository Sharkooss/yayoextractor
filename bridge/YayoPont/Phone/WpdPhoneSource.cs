using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using YayoPont.Core;

namespace YayoPont.Phone
{
    /// <summary>
    /// Le vrai téléphone, via Windows Portable Devices (ce qu'utilise l'Explorateur).
    /// Tous les appels COM passent par un seul thread : WPD n'aime pas les accès
    /// simultanés à un même appareil, et cela règle la question des apartments.
    /// Ouverture en lecture seule : le pont ne peut rien modifier sur le téléphone.
    /// </summary>
    internal sealed class WpdPhoneSource : IPhoneSource
    {
        private const int ErrorNotConnected = unchecked((int)0x8007048F);

        private readonly BlockingCollection<Action> queue = new BlockingCollection<Action>();
        private readonly Thread worker;
        private readonly Dictionary<string, PhoneFile> known = new Dictionary<string, PhoneFile>();

        private IPortableDeviceManager manager;
        private IPortableDevice device;
        private string deviceId;
        private PhoneInfo deviceInfo;

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

        public PhoneInfo Detect() => Run(DetectOnWorker);

        public IList<PhoneFile> Enumerate(Action<PhoneFile> found, CancellationToken cancel) =>
            Run(() => EnumerateOnWorker(found, cancel));

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
            Run<object>(() => { CloseDevice(); return null; });
            queue.CompleteAdding();
        }

        // ---------- exécuté sur le thread WPD ----------

        private PhoneInfo DetectOnWorker()
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

            if (deviceId != null && !ids.Contains(deviceId)) CloseDevice();
            if (deviceId != null)
            {
                try
                {
                    deviceInfo.State = HasStorage() ? "ready" : "locked";
                    return deviceInfo;
                }
                catch (IOException)
                {
                    CloseDevice(); // débranché entre deux sondages : on repart de zéro
                }
            }

            foreach (var id in ids)
            {
                try
                {
                    var info = TryOpen(id);
                    if (info != null) return info;
                }
                catch (COMException ex)
                {
                    Log.Error("ouverture de l'appareil " + id, ex);
                    CloseDevice();
                }
            }
            return null;
        }

        private PhoneInfo TryOpen(string id)
        {
            Trace("open: " + id);
            var candidate = Wpd.Create<IPortableDevice>(Wpd.ClsidDeviceFtm);
            Trace("open: créé");
            if (candidate.Open(id, ClientInfo()) < 0) { Release(candidate); return null; }
            Trace("open: ouvert");
            if (!candidate.Content(out var content).Ok() || !content.Properties(out var props).Ok()) { Release(candidate); return null; }
            Trace("open: contenu et propriétés");
            var values = Values(props, Wpd.DeviceObjectId, Wpd.DeviceProtocol, Wpd.DeviceFriendlyName, Wpd.DeviceManufacturer, Wpd.DeviceModel);
            Trace("open: valeurs " + (values != null));
            var protocol = String(values, Wpd.DeviceProtocol) ?? "";
            Trace("open: protocole " + protocol);
            // Les clés USB apparaissent aussi dans WPD (protocole « MSC ») : on les écarte.
            if (protocol.StartsWith("MSC", StringComparison.OrdinalIgnoreCase)) { Release(candidate); return null; }
            device = candidate;
            deviceId = id;
            var name = String(values, Wpd.DeviceFriendlyName);
            if (string.IsNullOrWhiteSpace(name)) name = String(values, Wpd.DeviceModel) ?? FriendlyName(id) ?? "Téléphone";
            deviceInfo = new PhoneInfo { Name = name, Manufacturer = String(values, Wpd.DeviceManufacturer) ?? "" };
            Trace("open: stockage ?");
            deviceInfo.State = HasStorage() ? "ready" : "locked";
            Log.Info($"téléphone détecté : {deviceInfo.Manufacturer} {name} ({protocol})");
            return deviceInfo;
        }

        /// <summary>
        /// Un Android verrouillé ou qui n'a pas encore « autorisé l'accès aux
        /// données » se montre, mais sans stockage.
        /// </summary>
        private bool HasStorage()
        {
            try { return Children(Wpd.DeviceObjectId).Count > 0; }
            catch (COMException) { return false; }
        }

        private IList<PhoneFile> EnumerateOnWorker(Action<PhoneFile> found, CancellationToken cancel)
        {
            if (device == null && DetectOnWorker() == null) throw new IOException("Aucun téléphone branché.");
            Check(device.Content(out var content));
            Check(content.Properties(out var props));
            var files = new List<PhoneFile>();
            foreach (var storage in Children(Wpd.DeviceObjectId))
            {
                Walk(storage, "", 0);
            }
            lock (known)
            {
                known.Clear();
                foreach (var file in files) known[file.Id] = file;
            }
            return files;

            void Walk(string parent, string relative, int depth)
            {
                foreach (var id in Children(parent))
                {
                    cancel.ThrowIfCancellationRequested();
                    var values = Values(props, id, Wpd.ObjectOriginalFileName, Wpd.ObjectName, Wpd.ObjectContentType,
                        Wpd.ObjectSize, Wpd.ObjectDateCreated, Wpd.ObjectDateModified);
                    if (values == null) continue;
                    var name = String(values, Wpd.ObjectOriginalFileName) ?? String(values, Wpd.ObjectName) ?? id;
                    var type = Guid(values, Wpd.ObjectContentType);
                    if (type == Wpd.ContentTypeFolder || type == Wpd.ContentTypeFunctionalObject)
                    {
                        if (depth >= MediaRules.MaxDepth || MediaRules.SkipFolder(name, relative)) continue;
                        Walk(id, relative.Length == 0 ? name : relative + "/" + name, depth + 1);
                        continue;
                    }
                    var kind = MediaRules.Kind(name);
                    if (kind == null) continue;
                    var rel = relative.Length == 0 ? name : relative + "/" + name;
                    files.Add(new PhoneFile
                    {
                        Id = id,
                        Name = name,
                        RelativePath = rel,
                        Size = (long)ULong(values, Wpd.ObjectSize),
                        Date = Date(values, Wpd.ObjectDateCreated) ?? Date(values, Wpd.ObjectDateModified),
                        Kind = kind,
                        Source = MediaRules.Source(rel),
                    });
                    found(files[files.Count - 1]);
                }
            }
        }

        private List<string> Children(string parent)
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
            if (device == null && DetectOnWorker() == null) throw new IOException("Aucun téléphone branché.");
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

        private void CloseDevice()
        {
            if (device != null)
            {
                try { device.Close(); } catch (COMException) { }
                Release(device);
            }
            device = null;
            deviceId = null;
            deviceInfo = null;
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
            k = Wpd.ClientMinorVersion; info.SetUnsignedIntegerValue(ref k, 0);
            k = Wpd.ClientRevision; info.SetUnsignedIntegerValue(ref k, 0);
            k = Wpd.ClientSecurityQos; info.SetUnsignedIntegerValue(ref k, Wpd.SecurityImpersonation);
            k = Wpd.ClientDesiredAccess; info.SetUnsignedIntegerValue(ref k, Wpd.GenericRead);
            return info;
        }

        private static IPortableDeviceValues Values(IPortableDeviceProperties props, string objectId, params PropertyKey[] keys)
        {
            var collection = Wpd.Create<IPortableDeviceKeyCollection>(Wpd.ClsidKeyCollection);
            foreach (var key in keys)
            {
                var k = key;
                collection.Add(ref k);
            }
            return props.GetValues(objectId, collection, out var values) < 0 ? null : values;
        }

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
