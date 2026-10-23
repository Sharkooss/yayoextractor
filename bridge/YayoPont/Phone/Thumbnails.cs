using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace YayoPont.Phone
{
    /// <summary>
    /// Réduction GDI+ (fournie avec Windows) quand le téléphone ne donne pas de
    /// vignette. Formats lisibles par GDI+ seulement : JPEG, PNG, GIF, BMP.
    /// </summary>
    internal static class Thumbnails
    {
        public const int MaxSide = 320;
        private const long MaxSourceBytes = 15 * 1024 * 1024;
        private static readonly string[] Decodable = { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

        public static bool CanDecode(string name) =>
            Decodable.Contains(Path.GetExtension(name ?? "").ToLowerInvariant());

        public static byte[] FromStream(Stream source, string name)
        {
            if (!CanDecode(name)) return null;
            try
            {
                using (var buffer = new MemoryStream())
                {
                    var chunk = new byte[256 * 1024];
                    int read;
                    while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        buffer.Write(chunk, 0, read);
                        if (buffer.Length > MaxSourceBytes) return null;
                    }
                    buffer.Position = 0;
                    using (var image = Image.FromStream(buffer, false, false)) return Shrink(image);
                }
            }
            catch (ArgumentException) { return null; } // image illisible
            catch (OutOfMemoryException) { return null; } // GDI+ signale ainsi un format inconnu
        }

        /// <summary>Remet aussi les vignettes du téléphone à l'endroit (orientation EXIF).</summary>
        public static byte[] Normalize(byte[] jpeg)
        {
            try
            {
                using (var image = Image.FromStream(new MemoryStream(jpeg), false, false))
                {
                    if (Orientation(image) == 1 && image.Width <= MaxSide && image.Height <= MaxSide) return jpeg;
                    return Shrink(image);
                }
            }
            catch (ArgumentException) { return jpeg; }
            catch (OutOfMemoryException) { return jpeg; }
        }

        private static byte[] Shrink(Image image)
        {
            ApplyOrientation(image);
            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)Math.Round(image.Width * scale));
            int h = Math.Max(1, (int)Math.Round(image.Height * scale));
            using (var thumb = new Bitmap(w, h))
            using (var g = Graphics.FromImage(thumb))
            using (var output = new MemoryStream())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Black);
                g.DrawImage(image, 0, 0, w, h);
                var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                var options = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(Encoder.Quality, 82L) } };
                thumb.Save(output, codec, options);
                return output.ToArray();
            }
        }

        private static int Orientation(Image image)
        {
            if (!image.PropertyIdList.Contains(0x0112)) return 1;
            var value = image.GetPropertyItem(0x0112).Value;
            return value.Length >= 2 ? BitConverter.ToUInt16(value, 0) : 1;
        }

        private static void ApplyOrientation(Image image)
        {
            switch (Orientation(image))
            {
                case 2: image.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                case 3: image.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                case 4: image.RotateFlip(RotateFlipType.Rotate180FlipX); break;
                case 5: image.RotateFlip(RotateFlipType.Rotate90FlipX); break;
                case 6: image.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                case 7: image.RotateFlip(RotateFlipType.Rotate270FlipX); break;
                case 8: image.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            }
        }
    }
}
