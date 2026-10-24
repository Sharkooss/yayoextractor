using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace YayoPont.Core
{
    /// <summary>
    /// Images d'une vidéo, décodées par Media Foundation (fourni avec Windows 10/11 ;
    /// absent des éditions « N » sans Media Feature Pack, HEVC seulement avec son
    /// extension). Sert à confirmer qu'une copie WhatsApp montre bien la même vidéo
    /// qu'un original : si quelque chose manque, on ne confirme rien.
    /// </summary>
    internal static class VideoFrames
    {
        private static readonly object Gate = new object();
        private static bool? started;

        /// <summary>Empreintes des images aux fractions de la durée données, null si la vidéo n'est pas décodable.</summary>
        public static List<VisualPrint> Prints(string path, double duration, int rotation, double[] fractions)
        {
            if (!Start()) return null;
            IMFAttributes attributes = null;
            IMFSourceReader reader = null;
            IMFMediaType type = null;
            try
            {
                Check(MFCreateAttributes(out attributes, 1));
                var processing = MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING;
                Check(attributes.SetUINT32(ref processing, 1));
                Check(MFCreateSourceReaderFromURL(path, attributes, out reader));
                Check(reader.SetStreamSelection(AllStreams, false));
                Check(reader.SetStreamSelection(FirstVideoStream, true));
                Check(MFCreateMediaType(out type));
                Guid major = MF_MT_MAJOR_TYPE, video = MFMediaType_Video, subtype = MF_MT_SUBTYPE, rgb32 = MFVideoFormat_RGB32;
                Check(type.SetGUID(ref major, ref video));
                Check(type.SetGUID(ref subtype, ref rgb32));
                Check(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, type));
                Release(type);
                type = null;
                Check(reader.GetCurrentMediaType(FirstVideoStream, out type));
                var frameSize = MF_MT_FRAME_SIZE;
                Check(type.GetUINT64(ref frameSize, out ulong size));
                int width = (int)(size >> 32), height = (int)(size & 0xFFFFFFFF);
                var strideKey = MF_MT_DEFAULT_STRIDE;
                int stride = type.GetUINT32(ref strideKey, out uint rawStride) == 0 ? (int)rawStride : width * 4;
                if (width <= 0 || height <= 0) return null;

                // Début réel (liste d'édition, première image non nulle) : premier échantillon lu.
                long start = -1;
                using (var first = Next(reader, -1, ref start, width, height, stride)) { }
                if (start < 0) return null;
                var prints = new List<VisualPrint>();
                foreach (var fraction in fractions)
                {
                    long target = start + (long)(fraction * duration * 1e7);
                    var position = new PropVariant { vt = 20, value = target }; // VT_I8, en 100 ns
                    var format = Guid.Empty;
                    Check(reader.SetCurrentPosition(ref format, ref position));
                    long ignored = 0;
                    using (var frame = Next(reader, target, ref ignored, width, height, stride))
                    {
                        if (frame == null) return null;
                        frame.RotateFlip(rotation == 90 ? RotateFlipType.Rotate90FlipNone
                            : rotation == 180 ? RotateFlipType.Rotate180FlipNone
                            : rotation == 270 ? RotateFlipType.Rotate270FlipNone : RotateFlipType.RotateNoneFlipNone);
                        var print = VisualPrint.FromImage(frame, 1);
                        if (print == null) return null;
                        prints.Add(print);
                    }
                }
                return prints;
            }
            catch (COMException ex)
            {
                Log.Error("décodage vidéo de " + path, ex);
                return null;
            }
            catch (InvalidCastException) { return null; }
            finally
            {
                Release(type);
                Release(reader);
                Release(attributes);
            }
        }

        /// <summary>La première image dont l'horodatage atteint <paramref name="target"/> (moins une demi-image), en bitmap.</summary>
        private static Bitmap Next(IMFSourceReader reader, long target, ref long first, int width, int height, int stride)
        {
            for (int k = 0; k < 900; k++)
            {
                Check(reader.ReadSample(FirstVideoStream, 0, out _, out uint flags, out long time, out IMFSample sample));
                try
                {
                    if ((flags & EndOfStream) != 0) return null;
                    if (sample == null) continue;
                    if (first < 0) first = time;
                    if (target >= 0 && time < target - 200000) continue; // 20 ms de marge
                    return ToBitmap(sample, width, height, stride);
                }
                finally { Release(sample); }
            }
            return null;
        }

        private static Bitmap ToBitmap(IMFSample sample, int width, int height, int stride)
        {
            IMFMediaBuffer buffer = null;
            try
            {
                Check(sample.ConvertToContiguousBuffer(out buffer));
                // Le décodeur aligne souvent les lignes (636 pixels rangés sur 640) : le pas vient d'IMF2DBuffer.
                var planar = buffer as IMF2DBuffer;
                IntPtr data;
                int pitch, length;
                if (planar != null && planar.Lock2D(out data, out pitch) == 0) length = int.MaxValue;
                else
                {
                    planar = null;
                    Check(buffer.Lock(out data, out _, out length));
                    pitch = length % height == 0 ? length / height : (stride != 0 ? stride : width * 4);
                }
                try
                {
                    int rowBytes = width * 4;
                    if (Math.Abs(pitch) < rowBytes || (planar == null && (long)Math.Abs(pitch) * height > length)) return null;
                    var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                    var target = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        var row = new byte[rowBytes];
                        for (int y = 0; y < height; y++)
                        {
                            // Pas négatif : image enregistrée de bas en haut (Lock2D donne alors la ligne du haut).
                            long source = planar != null ? (long)y * pitch : (stride < 0 ? (long)(height - 1 - y) * pitch : (long)y * pitch);
                            Marshal.Copy(new IntPtr(data.ToInt64() + source), row, 0, rowBytes);
                            Marshal.Copy(row, 0, target.Scan0 + y * target.Stride, rowBytes);
                        }
                    }
                    finally { bitmap.UnlockBits(target); }
                    return bitmap;
                }
                finally
                {
                    if (planar != null) planar.Unlock2D();
                    else buffer.Unlock();
                }
            }
            finally { Release(buffer); }
        }

        private static bool Start()
        {
            lock (Gate)
            {
                if (started == null)
                {
                    try { started = MFStartup(0x00020070, 1) == 0; } // MF_VERSION, MFSTARTUP_LITE
                    catch (DllNotFoundException) { started = false; }
                    catch (EntryPointNotFoundException) { started = false; }
                    if (started == false) Log.Info("Media Foundation indisponible : les copies WhatsApp de vidéos sont gardées.");
                }
                return started.Value;
            }
        }

        private static void Check(int hr)
        {
            if (hr < 0) throw new COMException("Media Foundation", hr);
        }

        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }

        // ---------- Interop Media Foundation (seules les méthodes utilisées ont une vraie signature) ----------

        private const uint FirstVideoStream = 0xFFFFFFFC;
        private const uint AllStreams = 0xFFFFFFFE;
        private const uint EndOfStream = 0x2;

        private static readonly Guid MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        private static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        private static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        private static readonly Guid MF_MT_FRAME_SIZE = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        private static readonly Guid MF_MT_DEFAULT_STRIDE = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        private static readonly Guid MFMediaType_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
        private static readonly Guid MFVideoFormat_RGB32 = new Guid("00000016-0000-0010-8000-00AA00389B71");

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public long value;
        }

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFStartup(uint version, uint flags);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateAttributes(out IMFAttributes attributes, uint size);

        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFCreateMediaType(out IMFMediaType type);

        [DllImport("mfreadwrite.dll", ExactSpelling = true)]
        private static extern int MFCreateSourceReaderFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IMFAttributes attributes, out IMFSourceReader reader);

        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFAttributes
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            [PreserveSig] int GetUINT32(ref Guid key, out uint value);
            [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, uint value);
            void SetUINT64(); void SetDouble();
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaType
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare();
            [PreserveSig] int GetUINT32(ref Guid key, out uint value);
            [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
            [PreserveSig] int SetUINT32(ref Guid key, uint value);
            void SetUINT64(); void SetDouble();
            [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
            void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
            void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
        }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSourceReader
        {
            void GetStreamSelection();
            [PreserveSig] int SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
            void GetNativeMediaType();
            [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
            [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
            [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref PropVariant position);
            [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample sample);
            void Flush(); void GetServiceForStream(); void GetPresentationAttribute();
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFSample
        {
            void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
            void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
            void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
            void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown();
            void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
            void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime(); void GetSampleDuration(); void SetSampleDuration();
            void GetBufferCount(); void GetBufferByIndex();
            [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            void AddBuffer(); void RemoveBufferByIndex(); void RemoveAllBuffers(); void GetTotalLength(); void CopyToBuffer();
        }

        [ComImport, Guid("7DC9D5F9-9ED9-44ec-9BBF-0600BB589FBB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMF2DBuffer
        {
            [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
            [PreserveSig] int Unlock2D();
            void GetScanline0AndPitch(); void IsContiguousFormat(); void GetContiguousLength(); void ContiguousCopyTo(); void ContiguousCopyFrom();
        }

        [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
            void GetCurrentLength(); void SetCurrentLength(); void GetMaxLength();
        }
    }
}
