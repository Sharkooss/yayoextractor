using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace YayoPont.Phone
{
    // Interfaces COM de Windows Portable Devices, déclarées à la main : le SDK
    // .NET ne sait pas importer les bibliothèques de types (pas de tlbimp).
    // L'ordre des méthodes reproduit EXACTEMENT celui des vtables de
    // PortableDeviceApi.h / PortableDeviceTypes.h (SDK 10.0.22621) ; une méthode
    // mal placée appellerait la mauvaise fonction. Les méthodes jamais appelées
    // ne servent qu'à garder les positions et restent des « Reserved ».

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>PROPVARIANT réduit à ce qu'on lit : le type et la valeur DATE.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        public const ushort VtDate = 7;
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public double Date;
        [FieldOffset(8)] public IntPtr Pointer;

        [DllImport("ole32.dll")]
        public static extern int PropVariantClear(ref PropVariant value);
    }

    [ComImport, Guid("a1567595-4c2f-4574-a6fa-ecef917b9a40"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceManager
    {
        [PreserveSig] int GetDevices([In, Out] IntPtr[] deviceIds, ref uint count);
        [PreserveSig] int RefreshDeviceList();
        [PreserveSig] int GetDeviceFriendlyName([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [In, Out] char[] name, ref uint length);
        [PreserveSig] int GetDeviceDescription([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [In, Out] char[] description, ref uint length);
        [PreserveSig] int GetDeviceManufacturer([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [In, Out] char[] manufacturer, ref uint length);
    }

    [ComImport, Guid("625e2df8-6392-4cf0-9ad1-3cfa5f17775c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDevice
    {
        [PreserveSig] int Open([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IPortableDeviceValues clientInfo);
        [PreserveSig] int SendCommand(uint flags, IPortableDeviceValues parameters, out IPortableDeviceValues results);
        [PreserveSig] int Content(out IPortableDeviceContent content);
        [PreserveSig] int Capabilities(out IntPtr capabilities);
        [PreserveSig] int Cancel();
        [PreserveSig] int Close();
    }

    [ComImport, Guid("6a96ed84-7c73-4480-9938-bf5af477d426"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceContent
    {
        [PreserveSig] int EnumObjects(uint flags, [MarshalAs(UnmanagedType.LPWStr)] string parentId, IPortableDeviceValues filter, out IEnumPortableDeviceObjectIDs objectIds);
        [PreserveSig] int Properties(out IPortableDeviceProperties properties);
        [PreserveSig] int Transfer(out IPortableDeviceResources resources);
    }

    [ComImport, Guid("10ece955-cf41-4728-bfa0-41eedf1bbf19"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumPortableDeviceObjectIDs
    {
        [PreserveSig] int Next(uint count, [Out] IntPtr[] objectIds, ref uint fetched);
    }

    [ComImport, Guid("7f6d695c-03df-4439-a809-59266beee3a6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceProperties
    {
        [PreserveSig] int GetSupportedProperties([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IntPtr keys);
        [PreserveSig] int GetPropertyAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IntPtr attributes);
        [PreserveSig] int GetValues([MarshalAs(UnmanagedType.LPWStr)] string objectId, IPortableDeviceKeyCollection keys, out IPortableDeviceValues values);
    }

    [ComImport, Guid("fd8878ac-d841-4d17-891c-e6829cdb6934"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceResources
    {
        [PreserveSig] int GetSupportedResources([MarshalAs(UnmanagedType.LPWStr)] string objectId, out IntPtr keys);
        [PreserveSig] int GetResourceAttributes([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, out IntPtr attributes);
        [PreserveSig] int GetStream([MarshalAs(UnmanagedType.LPWStr)] string objectId, ref PropertyKey key, uint mode, ref uint optimalBufferSize, out IStream stream);
    }

    [ComImport, Guid("6848f6f2-3155-4f86-b6f5-263eeeab3143"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceValues
    {
        [PreserveSig] int GetCount(out uint count);                                                   // 1
        [PreserveSig] int GetAt(uint index, IntPtr key, IntPtr value);                                // 2
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);                       // 3
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);                       // 4
        [PreserveSig] int SetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] string value); // 5
        [PreserveSig] int GetStringValue(ref PropertyKey key, [MarshalAs(UnmanagedType.LPWStr)] out string value); // 6
        [PreserveSig] int SetUnsignedIntegerValue(ref PropertyKey key, uint value);                   // 7
        [PreserveSig] int GetUnsignedIntegerValue(ref PropertyKey key, out uint value);               // 8
        [PreserveSig] int SetSignedIntegerValue(ref PropertyKey key, int value);                      // 9
        [PreserveSig] int GetSignedIntegerValue(ref PropertyKey key, out int value);                  // 10
        [PreserveSig] int SetUnsignedLargeIntegerValue(ref PropertyKey key, ulong value);             // 11
        [PreserveSig] int GetUnsignedLargeIntegerValue(ref PropertyKey key, out ulong value);         // 12
        [PreserveSig] int Reserved13();  // SetSignedLargeIntegerValue
        [PreserveSig] int Reserved14();  // GetSignedLargeIntegerValue
        [PreserveSig] int Reserved15();  // SetFloatValue
        [PreserveSig] int Reserved16();  // GetFloatValue
        [PreserveSig] int Reserved17();  // SetErrorValue
        [PreserveSig] int Reserved18();  // GetErrorValue
        [PreserveSig] int Reserved19();  // SetKeyValue
        [PreserveSig] int Reserved20();  // GetKeyValue
        [PreserveSig] int Reserved21();  // SetBoolValue
        [PreserveSig] int Reserved22();  // GetBoolValue
        [PreserveSig] int Reserved23();  // SetIUnknownValue
        [PreserveSig] int Reserved24();  // GetIUnknownValue
        [PreserveSig] int SetGuidValue(ref PropertyKey key, ref Guid value);                          // 25
        [PreserveSig] int GetGuidValue(ref PropertyKey key, out Guid value);                          // 26
    }

    [ComImport, Guid("dada2357-e0ad-492e-98db-dd61c53ba353"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPortableDeviceKeyCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, ref PropertyKey key);
        [PreserveSig] int Add(ref PropertyKey key);
        [PreserveSig] int Clear();
        [PreserveSig] int RemoveAt(uint index);
    }

    internal static class Wpd
    {
        public static readonly Guid ClsidManager = new Guid("0af10cec-2ecd-4b92-9581-34f6ae0637f3");
        public static readonly Guid ClsidDeviceFtm = new Guid("f7c0039a-4762-488a-b4b3-760ef9a1ba9b");
        public static readonly Guid ClsidValues = new Guid("0c15d503-d017-47ce-9016-7b3f978721cc");
        public static readonly Guid ClsidKeyCollection = new Guid("de2d022d-2480-43be-97f0-d1fa2cf98f4f");

        public const string DeviceObjectId = "DEVICE";
        public const uint StgmRead = 0;
        public const uint GenericRead = 0x80000000;
        public const uint SecurityImpersonation = 0x00020000;

        private static readonly Guid ObjectProps = new Guid("ef6b490d-5cd8-437a-affc-da8b60ee4a3c");
        private static readonly Guid DeviceProps = new Guid("26d4979a-e643-4626-9e2b-736dc0c92fdc");
        private static readonly Guid ClientProps = new Guid("204d9f0c-2292-4080-9f42-40664e70f859");

        public static readonly PropertyKey ObjectName = new PropertyKey(ObjectProps, 4);
        public static readonly PropertyKey ObjectContentType = new PropertyKey(ObjectProps, 7);
        public static readonly PropertyKey ObjectSize = new PropertyKey(ObjectProps, 11);
        public static readonly PropertyKey ObjectOriginalFileName = new PropertyKey(ObjectProps, 12);
        public static readonly PropertyKey ObjectDateCreated = new PropertyKey(ObjectProps, 18);
        public static readonly PropertyKey ObjectDateModified = new PropertyKey(ObjectProps, 19);
        public static readonly PropertyKey DeviceProtocol = new PropertyKey(DeviceProps, 6);
        public static readonly PropertyKey DeviceManufacturer = new PropertyKey(DeviceProps, 7);
        public static readonly PropertyKey DeviceModel = new PropertyKey(DeviceProps, 8);
        public static readonly PropertyKey DeviceFriendlyName = new PropertyKey(DeviceProps, 12);
        public static readonly PropertyKey ClientName = new PropertyKey(ClientProps, 2);
        public static readonly PropertyKey ClientMajorVersion = new PropertyKey(ClientProps, 3);
        public static readonly PropertyKey ClientMinorVersion = new PropertyKey(ClientProps, 4);
        public static readonly PropertyKey ClientRevision = new PropertyKey(ClientProps, 5);
        public static readonly PropertyKey ClientSecurityQos = new PropertyKey(ClientProps, 8);
        public static readonly PropertyKey ClientDesiredAccess = new PropertyKey(ClientProps, 9);
        public static readonly PropertyKey ResourceDefault = new PropertyKey(new Guid("e81e79be-34f0-41bf-b53f-f1a06ae87842"), 0);
        public static readonly PropertyKey ResourceThumbnail = new PropertyKey(new Guid("c7c407ba-98fa-46b5-9960-23fec124cfde"), 0);

        public static readonly Guid ContentTypeFolder = new Guid("27e2e392-a111-48e0-ab0c-e17705a05f85");
        public static readonly Guid ContentTypeFunctionalObject = new Guid("99ed0160-17ff-4c44-9d98-1d7a6f941921");

        public static T Create<T>(Guid clsid) => (T)Activator.CreateInstance(Type.GetTypeFromCLSID(clsid, true));
    }
}
