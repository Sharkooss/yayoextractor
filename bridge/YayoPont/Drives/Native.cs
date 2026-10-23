using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace YayoPont.Drives
{
    /// <summary>Appels Windows bas niveau : bus USB d'un lecteur, éjection, appareils USB présents.</summary>
    internal static class Native
    {
        private const uint IoctlStorageQueryProperty = 0x002D1400;
        private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
        private const int BusTypeUsb = 7;
        private const uint FileShareReadWrite = 3;
        private const uint OpenExisting = 3;
        private const uint DigcfPresent = 0x2;
        private const uint DigcfAllClasses = 0x4;
        private const uint DigcfDeviceInterface = 0x10;
        private static Guid GuidDevinterfaceDisk = new Guid("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

        /// <summary>Le volume est-il sur un périphérique USB ? Donne aussi son nom commercial.</summary>
        public static bool IsUsbVolume(string letter, out string product)
        {
            product = null;
            using (var handle = OpenVolume(letter))
            {
                if (handle.IsInvalid) return false;
                var query = new byte[12]; // PropertyId = StorageDeviceProperty (0), QueryType = standard (0)
                var output = Marshal.AllocHGlobal(1024);
                try
                {
                    if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, output, 1024, out _, IntPtr.Zero)) return false;
                    var vendor = AnsiAt(output, Marshal.ReadInt32(output, 12));
                    var model = AnsiAt(output, Marshal.ReadInt32(output, 16));
                    var name = (vendor + " " + model).Trim();
                    product = name.Length > 0 ? name : null;
                    return Marshal.ReadInt32(output, 28) == BusTypeUsb;
                }
                finally
                {
                    Marshal.FreeHGlobal(output);
                }
            }
        }

        /// <summary>
        /// « Retirer le périphérique en toute sécurité » : on retrouve le disque du
        /// volume, puis on demande à Windows d'éjecter son parent USB. Si Windows
        /// refuse (fichier ouvert), on le dit plutôt que de forcer.
        /// </summary>
        public static bool Eject(string letter, out string message)
        {
            message = "La clé est encore utilisée. Ferme les fenêtres qui l'affichent, puis réessaie.";
            int devInst = DiskDevInst(letter);
            if (devInst != 0)
            {
                var targets = new List<int>();
                if (CM_Get_Parent(out int parent, devInst, 0) == 0) targets.Add(parent);
                targets.Add(devInst);
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    foreach (var target in targets)
                    {
                        var veto = new StringBuilder(260);
                        if (CM_Request_Device_EjectW(target, out int vetoType, veto, veto.Capacity, 0) == 0 && vetoType == 0)
                        {
                            message = "C'est bon, tu peux retirer la clé.";
                            return true;
                        }
                    }
                    Thread.Sleep(800); // laisse Windows finir d'écrire
                }
            }
            if (ShellEject(letter))
            {
                message = "C'est bon, tu peux retirer la clé.";
                return true;
            }
            return false;
        }

        /// <summary>Instances USB présentes (« USB\VID_04E8&amp;PID_6860\… »).</summary>
        public static IEnumerable<(int vid, int pid)> PresentUsbDevices()
        {
            var set = SetupDiGetClassDevs(IntPtr.Zero, "USB", IntPtr.Zero, DigcfPresent | DigcfAllClasses);
            if (set == new IntPtr(-1)) yield break;
            try
            {
                var data = new SpDevinfoData { CbSize = Marshal.SizeOf(typeof(SpDevinfoData)) };
                var id = new StringBuilder(512);
                for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                {
                    if (!SetupDiGetDeviceInstanceId(set, ref data, id, id.Capacity, out _)) continue;
                    if (TryParseVidPid(id.ToString(), out int vid, out int pid)) yield return (vid, pid);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        internal static bool TryParseVidPid(string instanceId, out int vid, out int pid)
        {
            vid = pid = 0;
            var upper = instanceId.ToUpperInvariant();
            int v = upper.IndexOf("VID_", StringComparison.Ordinal);
            int p = upper.IndexOf("PID_", StringComparison.Ordinal);
            return v >= 0 && p >= 0 && v + 8 <= upper.Length && p + 8 <= upper.Length
                && int.TryParse(upper.Substring(v + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vid)
                && int.TryParse(upper.Substring(p + 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out pid);
        }

        private static int DiskDevInst(string letter)
        {
            if (!DeviceNumber(letter, out int type, out int number)) return 0;
            var set = SetupDiGetClassDevs(ref GuidDevinterfaceDisk, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == new IntPtr(-1)) return 0;
            try
            {
                var iface = new SpDeviceInterfaceData { CbSize = Marshal.SizeOf(typeof(SpDeviceInterfaceData)) };
                for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref GuidDevinterfaceDisk, i, ref iface); i++)
                {
                    var info = new SpDevinfoData { CbSize = Marshal.SizeOf(typeof(SpDevinfoData)) };
                    SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out int required, ref info);
                    if (required <= 0) continue;
                    var detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, ref info)) continue;
                        var path = Marshal.PtrToStringUni(detail + 4);
                        using (var disk = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero))
                        {
                            if (!disk.IsInvalid && DeviceNumber(disk, out int diskType, out int diskNumber)
                                && diskType == type && diskNumber == number) return info.DevInst;
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return 0;
        }

        private static bool DeviceNumber(string letter, out int type, out int number)
        {
            using (var handle = OpenVolume(letter))
            {
                type = number = -1;
                return !handle.IsInvalid && DeviceNumber(handle, out type, out number);
            }
        }

        private static bool DeviceNumber(SafeFileHandle handle, out int type, out int number)
        {
            type = number = -1;
            var output = Marshal.AllocHGlobal(12);
            try
            {
                if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, null, 0, output, 12, out _, IntPtr.Zero)) return false;
                type = Marshal.ReadInt32(output, 0);
                number = Marshal.ReadInt32(output, 4);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(output);
            }
        }

        private static SafeFileHandle OpenVolume(string letter) =>
            CreateFile(@"\\.\" + letter.TrimEnd(':', '\\') + ":", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        private static string AnsiAt(IntPtr buffer, int offset) =>
            offset > 0 && offset < 1024 ? (Marshal.PtrToStringAnsi(buffer + offset) ?? "").Trim() : "";

        /// <summary>Repli : le verbe « Éjecter » de l'Explorateur.</summary>
        private static bool ShellEject(string letter)
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                var shell = Activator.CreateInstance(type);
                var computer = type.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { 17 });
                var item = computer.GetType().InvokeMember("ParseName", System.Reflection.BindingFlags.InvokeMethod, null, computer,
                    new object[] { letter.TrimEnd(':', '\\') + ":\\" });
                if (item == null) return false;
                item.GetType().InvokeMember("InvokeVerb", System.Reflection.BindingFlags.InvokeMethod, null, item, new object[] { "Eject" });
                Thread.Sleep(1500);
                return !System.IO.Directory.Exists(letter.TrimEnd(':', '\\') + ":\\");
            }
            catch (Exception ex)
            {
                Log.Error("éjection par l'Explorateur", ex);
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDevinfoData
        {
            public int CbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int CbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceGuid, uint index, ref SpDeviceInterfaceData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int detailSize, out int required, ref SpDevinfoData info);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevinfoData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevinfoData data, StringBuilder id, int size, out int required);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("cfgmgr32.dll")]
        private static extern int CM_Get_Parent(out int parent, int devInst, int flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        private static extern int CM_Request_Device_EjectW(int devInst, out int vetoType, StringBuilder vetoName, int nameLength, int flags);
    }
}
