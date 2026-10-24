using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace YayoPont.Core
{
    /// <summary>
    /// Empreinte visuelle d'une photo, insensible à la recompression et à la taille
    /// (copies WhatsApp « Sent ») : dHash 64 bits pour trier vite, puis luminance
    /// 32×32 et couleurs 8×8 pour confirmer. L'orientation EXIF est appliquée.
    /// </summary>
    internal sealed class VisualPrint
    {
        private const int Fine = 32;
        private const int Color = 8;
        private const int Reduced = 128;

        public ulong DHash;
        public double Aspect;
        public double Spread;
        private float[] luma;
        private float[] rgb;

        /// <summary>Distance de Hamming entre deux dHash (0 à 64).</summary>
        public int Distance(VisualPrint other)
        {
            ulong x = DHash ^ other.DHash;
            int n = 0;
            while (x != 0) { x &= x - 1; n++; }
            return n;
        }

        public static bool SameAspect(double a, double b, double tolerance) =>
            a > 0 && b > 0 && Math.Abs(a - b) / Math.Max(a, b) <= tolerance;

        /// <summary>
        /// Première étape (vignette ou image entière) : même cadrage à 3 % près et
        /// dHash proche.
        /// </summary>
        public bool Resembles(VisualPrint other) =>
            other != null && SameAspect(Aspect, other.Aspect, 0.03) && Distance(other) <= 12;

        /// <summary>
        /// Seconde étape, sur les deux images entières : même cadrage à 2 % près, dHash
        /// très proche, luminance corrélée à 0,95 et écart moyen faible, mêmes couleurs.
        /// Une image presque unie (ciel, mur) ne se confirme jamais : trop ambiguë.
        /// </summary>
        public bool Confirms(VisualPrint other)
        {
            if (other == null || luma == null || other.luma == null) return false;
            if (!SameAspect(Aspect, other.Aspect, 0.02) || Distance(other) > 8) return false;
            if (Spread < 0.04 || other.Spread < 0.04) return false;
            var (correlation, diff, colour) = Measures(other);
            return correlation >= 0.95 && diff <= 0.05 && colour <= 0.06;
        }

        /// <summary>Corrélation de la luminance 32×32, écart moyen de luminance, écart moyen des couleurs 8×8.</summary>
        internal (double correlation, double diff, double colour) Measures(VisualPrint other)
        {
            double meanA = Mean(luma), meanB = Mean(other.luma);
            double cov = 0, varA = 0, varB = 0, diff = 0;
            for (int i = 0; i < luma.Length; i++)
            {
                double a = luma[i] - meanA, b = other.luma[i] - meanB;
                cov += a * b;
                varA += a * a;
                varB += b * b;
                diff += Math.Abs(luma[i] - other.luma[i]);
            }
            double correlation = cov / Math.Sqrt(varA * varB);
            double colour = 0;
            for (int i = 0; i < rgb.Length; i++) colour += Math.Abs(rgb[i] - other.rgb[i]);
            return (double.IsNaN(correlation) ? 0 : correlation, diff / luma.Length, colour / rgb.Length);
        }

        /// <summary>Empreinte d'une image JPEG/PNG en mémoire, null si illisible.</summary>
        public static VisualPrint FromBytes(byte[] data, int offset, int length, int orientation)
        {
            try
            {
                using (var stream = new MemoryStream(data, offset, length, false))
                using (var image = Image.FromStream(stream, false, false))
                    return FromImage(image, orientation);
            }
            catch (ArgumentException) { return null; }
            catch (ExternalException) { return null; }
            catch (OutOfMemoryException) { return null; } // GDI+ : format illisible
        }

        /// <summary>Empreinte d'un fichier image entier (orientation lue dans l'image), null si illisible.</summary>
        public static VisualPrint FromFile(string path)
        {
            try
            {
                var data = File.ReadAllBytes(path);
                using (var stream = new MemoryStream(data, false))
                using (var image = Image.FromStream(stream, false, false))
                    return FromImage(image, Orientation(image));
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (ExternalException) { return null; }
            catch (OutOfMemoryException) { return null; }
        }

        private static int Orientation(Image image)
        {
            foreach (var id in image.PropertyIdList)
            {
                if (id != 0x0112) continue;
                var value = image.GetPropertyItem(id).Value;
                int o = value != null && value.Length >= 2 ? value[0] | (value[1] << 8) : 1;
                return o >= 1 && o <= 8 ? o : 1;
            }
            return 1;
        }

        private static bool Swaps(int orientation) => orientation >= 5 && orientation <= 8;

        internal static VisualPrint FromImage(Image image, int orientation)
        {
            if (image.Width < 8 || image.Height < 8) return null;
            var print = new VisualPrint
            {
                Aspect = Swaps(orientation) ? image.Height / (double)image.Width : image.Width / (double)image.Height,
            };
            // Une seule réduction coûteuse de la grande image, puis les trois petites tailles depuis celle-ci.
            if (image.Width > Reduced * 2 && image.Height > Reduced * 2)
            {
                using (var reduced = new Bitmap(Reduced, Reduced, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(reduced))
                    using (var attributes = new ImageAttributes())
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        attributes.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(image, new Rectangle(0, 0, Reduced, Reduced), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                    }
                    Fill(print, reduced, orientation);
                }
            }
            else Fill(print, image, orientation);
            return print;
        }

        private static void Fill(VisualPrint print, Image image, int orientation)
        {
            // dHash : 9×8 après orientation (on dessine en 8×9 si l'image sera tournée).
            var small = Pixels(image, orientation, 9, 8);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    if (Luma(small, y * 9 + x) > Luma(small, y * 9 + x + 1)) print.DHash |= 1UL << (y * 8 + x);
            var fine = Pixels(image, orientation, Fine, Fine);
            print.luma = new float[Fine * Fine];
            for (int i = 0; i < print.luma.Length; i++) print.luma[i] = (float)Luma(fine, i);
            double mean = Mean(print.luma), spread = 0;
            foreach (var v in print.luma) spread += (v - mean) * (v - mean);
            print.Spread = Math.Sqrt(spread / print.luma.Length);
            var colour = Pixels(image, orientation, Color, Color);
            print.rgb = new float[Color * Color * 3];
            for (int i = 0; i < Color * Color; i++)
            {
                print.rgb[i * 3] = colour[i * 3] / 255f;
                print.rgb[i * 3 + 1] = colour[i * 3 + 1] / 255f;
                print.rgb[i * 3 + 2] = colour[i * 3 + 2] / 255f;
            }
        }

        /// <summary>L'image réduite à <paramref name="w"/>×<paramref name="h"/> après orientation : octets R, V, B.</summary>
        private static byte[] Pixels(Image image, int orientation, int w, int h)
        {
            bool swap = Swaps(orientation);
            int dw = swap ? h : w, dh = swap ? w : h;
            using (var bitmap = new Bitmap(dw, dh, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                using (var attributes = new ImageAttributes())
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY); // pas de bord sombre
                    g.DrawImage(image, new Rectangle(0, 0, dw, dh), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
                }
                bitmap.RotateFlip(Rotation(orientation));
                var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    var row = new byte[data.Stride];
                    var result = new byte[w * h * 3];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                        for (int x = 0; x < w; x++)
                        {
                            result[(y * w + x) * 3] = row[x * 3 + 2];
                            result[(y * w + x) * 3 + 1] = row[x * 3 + 1];
                            result[(y * w + x) * 3 + 2] = row[x * 3];
                        }
                    }
                    return result;
                }
                finally { bitmap.UnlockBits(data); }
            }
        }

        private static RotateFlipType Rotation(int orientation)
        {
            switch (orientation)
            {
                case 2: return RotateFlipType.RotateNoneFlipX;
                case 3: return RotateFlipType.Rotate180FlipNone;
                case 4: return RotateFlipType.RotateNoneFlipY;
                case 5: return RotateFlipType.Rotate90FlipX;
                case 6: return RotateFlipType.Rotate90FlipNone;
                case 7: return RotateFlipType.Rotate270FlipX;
                case 8: return RotateFlipType.Rotate270FlipNone;
                default: return RotateFlipType.RotateNoneFlipNone;
            }
        }

        private static double Luma(byte[] rgb, int pixel) =>
            (0.299 * rgb[pixel * 3] + 0.587 * rgb[pixel * 3 + 1] + 0.114 * rgb[pixel * 3 + 2]) / 255.0;

        private static double Mean(float[] values)
        {
            double sum = 0;
            foreach (var v in values) sum += v;
            return sum / values.Length;
        }
    }
}
