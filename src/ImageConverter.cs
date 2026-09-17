// Конвертер картинок: WinForms UI.
//  - decoding through WIC (WPF imaging): JPG/PNG/WebP/HEIC/AVIF/RAW/JPEG XL/... whenever Windows has the codec;
//  - encoding through GDI+ (PNG/JPG/BMP/TIFF/GIF), WinRT Windows.Graphics.Imaging (HEIC, JPEG XR — these
//    encoders ship as Store packages that WPF can't see), or external encoders dropped into .\tools
//    (cwebp.exe -> WebP, avifenc.exe -> AVIF, cjxl.exe -> JPEG XL).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using WM = System.Windows.Media;

namespace ImageConverter
{
    enum EncKind { Gdi, WinRt, External }

    class ConvertResult
    {
        public int Quality;            // quality actually used (0 for lossless formats)
        public Size Size;              // pixel size written
        public bool Downscaled;        // shrunk to meet the size limit
        public bool MissedTarget;      // even the smallest attempt is over the limit
    }

    class OutFormat
    {
        public readonly string Ext, Tool; public readonly EncKind Kind;
        public readonly ImageFormat Gdi; public readonly Guid EncoderId; public readonly bool HasQuality;
        readonly string name;

        static readonly Dictionary<string, string> EnglishNames = new Dictionary<string, string> {
            { ".png", "PNG (lossless)" }, { ".heic", "HEIC (like on iPhone)" } };

        public string Name
        {
            get { string en; return Lang.En && EnglishNames.TryGetValue(Ext, out en) ? en : name; }
        }

        OutFormat(string name, string ext, EncKind kind, ImageFormat gdi, Guid id, string tool, bool quality)
        {
            this.name = name; Ext = ext; Kind = kind; Gdi = gdi; EncoderId = id; Tool = tool; HasQuality = quality;
        }
        static OutFormat G(string n, string e, ImageFormat f, bool q) { return new OutFormat(n, e, EncKind.Gdi, f, Guid.Empty, null, q); }
        static OutFormat R(string n, string e, string id, bool q) { return new OutFormat(n, e, EncKind.WinRt, null, new Guid(id), null, q); }
        static OutFormat X(string n, string e, string tool) { return new OutFormat(n, e, EncKind.External, null, Guid.Empty, tool, true); }

        public override string ToString() { return Name; }

        public static readonly OutFormat[] Known = {
            G("PNG (без потерь)", ".png", ImageFormat.Png, false),
            G("JPG", ".jpg", ImageFormat.Jpeg, true),
            R("HEIC (как на iPhone)", ".heic", "0dbecec1-9eb3-4860-9c6f-ddbe86634575", true),
            X("WebP", ".webp", "cwebp.exe"),
            X("AVIF", ".avif", "avifenc.exe"),
            X("JPEG XL", ".jxl", "cjxl.exe"),
            R("JPEG XR", ".jxr", "ac4ce3cb-e1c1-44cd-8215-5a1665509ec2", true),
            G("BMP", ".bmp", ImageFormat.Bmp, false),
            G("TIFF", ".tif", ImageFormat.Tiff, false),
            G("GIF", ".gif", ImageFormat.Gif, false),
        };

        public static string ToolsDir { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "tools"); } }
        public string ToolPath { get { return Tool == null ? null : Path.Combine(ToolsDir, Tool); } }

        public bool IsAvailable
        {
            get
            {
                switch (Kind)
                {
                    case EncKind.WinRt: return WinRtImaging.HasEncoder(EncoderId);
                    case EncKind.External: return File.Exists(ToolPath);
                    default: return true;
                }
            }
        }

        public static OutFormat[] Available() { return Known.Where(f => f.IsAvailable).ToArray(); }

        public static OutFormat FromExt(string ext)
        {
            ext = ext.ToLowerInvariant();
            if (ext == ".jpeg") ext = ".jpg";
            if (ext == ".tiff") ext = ".tif";
            if (ext == ".heif") ext = ".heic";
            if (ext == ".wdp") ext = ".jxr";
            return Known.FirstOrDefault(f => f.Ext == ext);
        }
    }

    // Windows.Graphics.Imaging through reflection (no .winmd references needed at compile time)
    static class WinRtImaging
    {
        const string Img = ", Windows.Graphics.Imaging, ContentType=WindowsRuntime";
        static Type tEnc, tPF, tAM, tTV, tPT, tExt, tStreamExt;
        static HashSet<Guid> encoders;
        static bool initTried;

        static bool Init()
        {
            if (initTried) return tEnc != null;
            initTried = true;
            try
            {
                Assembly sysRt = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089");
                tExt = sysRt.GetType("System.WindowsRuntimeSystemExtensions", true);
                tStreamExt = sysRt.GetType("System.IO.WindowsRuntimeStreamExtensions", true);
                tEnc = Type.GetType("Windows.Graphics.Imaging.BitmapEncoder" + Img, true);
                tPF = Type.GetType("Windows.Graphics.Imaging.BitmapPixelFormat" + Img, true);
                tAM = Type.GetType("Windows.Graphics.Imaging.BitmapAlphaMode" + Img, true);
                tTV = Type.GetType("Windows.Graphics.Imaging.BitmapTypedValue" + Img, true);
                tPT = Type.GetType("Windows.Foundation.PropertyType, Windows.Foundation, ContentType=WindowsRuntime", true);
                encoders = new HashSet<Guid>();
                IEnumerable list = (IEnumerable)tEnc.GetMethod("GetEncoderInformationEnumerator").Invoke(null, null);
                foreach (object info in list) encoders.Add((Guid)info.GetType().GetProperty("CodecId").GetValue(info, null));
                return true;
            }
            catch { tEnc = null; return false; }
        }

        public static bool HasEncoder(Guid id) { return Init() && encoders.Contains(id); }

        static object Await(object op, Type resultType)
        {
            MethodInfo m = tExt.GetMethods().First(x => x.Name == "AsTask" && x.GetParameters().Length == 1
                && x.GetParameters()[0].ParameterType.Name == "IAsyncOperation`1").MakeGenericMethod(resultType);
            Task t = (Task)m.Invoke(null, new[] { op });
            t.Wait();
            return t.GetType().GetProperty("Result").GetValue(t, null);
        }

        static void AwaitAction(object act)
        {
            MethodInfo m = tExt.GetMethods().First(x => x.Name == "AsTask" && x.GetParameters().Length == 1
                && x.GetParameters()[0].ParameterType.Name == "IAsyncAction");
            ((Task)m.Invoke(null, new[] { act })).Wait();
        }

        // bgra: tightly packed 32bpp BGRA rows
        public static void Encode(Guid encoderId, byte[] bgra, int w, int h, string path, int quality)
        {
            if (!Init()) throw new InvalidOperationException(Lang.T("WinRT-кодировщики недоступны", "WinRT encoders are not available"));
            using (FileStream fs = File.Create(path))
            {
                object ras = tStreamExt.GetMethod("AsRandomAccessStream", new[] { typeof(Stream) }).Invoke(null, new object[] { fs });
                try
                {
                    object op;
                    if (quality > 0)
                    {
                        Type kvT = typeof(KeyValuePair<,>).MakeGenericType(typeof(string), tTV);
                        Type listT = typeof(List<>).MakeGenericType(kvT);
                        object opts = Activator.CreateInstance(listT);
                        object tv = Activator.CreateInstance(tTV, new object[] { (float)(Math.Min(100, quality) / 100.0), Enum.Parse(tPT, "Single") });
                        listT.GetMethod("Add").Invoke(opts, new[] { Activator.CreateInstance(kvT, "ImageQuality", tv) });
                        MethodInfo c3 = tEnc.GetMethods().First(m => m.Name == "CreateAsync" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(Guid));
                        op = c3.Invoke(null, new object[] { encoderId, ras, opts });
                    }
                    else
                    {
                        MethodInfo c2 = tEnc.GetMethods().First(m => m.Name == "CreateAsync" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(Guid));
                        op = c2.Invoke(null, new object[] { encoderId, ras });
                    }
                    object enc = Await(op, tEnc);
                    tEnc.GetMethod("SetPixelData").Invoke(enc, new object[] {
                        Enum.Parse(tPF, "Bgra8"), Enum.Parse(tAM, "Ignore"), (uint)w, (uint)h, 96.0, 96.0, bgra });
                    AwaitAction(tEnc.GetMethod("FlushAsync").Invoke(enc, null));
                }
                finally { IDisposable d = ras as IDisposable; if (d != null) d.Dispose(); }
            }
        }
    }

    static class Converter
    {
        public static readonly string[] InputExt = {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".webp",
            ".heic", ".heif", ".hif", ".avif", ".jxl", ".jxr", ".wdp", ".ico", ".cur", ".dds",
            // camera RAW (Microsoft Raw Image Extension)
            ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".raf", ".orf",
            ".rw2", ".rwl", ".pef", ".srw", ".3fr", ".erf", ".kdc", ".mrw", ".mos", ".x3f", ".raw", ".iiq" };

        public static bool IsImage(string path)
        {
            return InputExt.Contains(Path.GetExtension(path).ToLowerInvariant());
        }

        public static Size ReadSize(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            {
                BitmapDecoder dec = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                BitmapFrame fr = dec.Frames[0];
                int o = ReadOrientation(fr);
                // orientations 5-8 are rotated by 90 degrees: show the size the output will have
                return o >= 5 && o <= 8 ? new Size(fr.PixelHeight, fr.PixelWidth) : new Size(fr.PixelWidth, fr.PixelHeight);
            }
        }

        static int ReadOrientation(BitmapFrame fr)
        {
            BitmapMetadata md;
            try { md = fr.Metadata as BitmapMetadata; } catch { return 1; }
            if (md == null) return 1;
            foreach (string q in new[] { "System.Photo.Orientation", "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
            {
                try
                {
                    object o = md.GetQuery(q);
                    if (o != null) return Convert.ToInt32(o);
                }
                catch { }
            }
            return 1;
        }

        static BitmapSource ApplyOrientation(BitmapSource s, int o)
        {
            double angle = 0; bool flip = false;
            switch (o)
            {
                case 2: flip = true; break;
                case 3: angle = 180; break;
                case 4: angle = 180; flip = true; break;
                case 5: angle = 90; flip = true; break;
                case 6: angle = 90; break;
                case 7: angle = 270; flip = true; break;
                case 8: angle = 270; break;
                default: return s;
            }
            WM.TransformGroup tg = new WM.TransformGroup();
            if (flip) tg.Children.Add(new WM.ScaleTransform(-1, 1));
            if (angle != 0) tg.Children.Add(new WM.RotateTransform(angle));
            return new TransformedBitmap(s, tg);
        }

        public static Bitmap Load(string path)
        {
            BitmapSource src;
            using (FileStream fs = File.OpenRead(path))
            {
                BitmapDecoder dec = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame fr = dec.Frames[0];
                src = ApplyOrientation(fr, ReadOrientation(fr));
            }
            return ToGdi(src);
        }

        // Small preview that covers maxW x maxH (decoded at reduced size when the codec supports it)
        public static Bitmap LoadThumb(string path, int maxW, int maxH)
        {
            int o, pw, ph;
            using (FileStream fs = File.OpenRead(path))
            {
                BitmapFrame fr = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                o = ReadOrientation(fr); pw = fr.PixelWidth; ph = fr.PixelHeight;
            }
            bool rot = o >= 5 && o <= 8;
            int tw = rot ? maxH : maxW, th = rot ? maxW : maxH;
            double s = Math.Min(1.0, Math.Max((double)tw / pw, (double)th / ph));
            BitmapImage bi = new BitmapImage();
            using (FileStream fs = File.OpenRead(path))
            {
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = fs;
                bi.DecodePixelWidth = Math.Max(1, (int)Math.Round(pw * s));
                bi.EndInit();
            }
            bi.Freeze();
            Bitmap b = ToGdi(ApplyOrientation(bi, o));
            // some codecs ignore DecodePixelWidth: shrink here so previews stay light
            int limitW = maxW * 2, limitH = maxH * 2;
            if (b.Width > limitW || b.Height > limitH)
            {
                double f = Math.Max((double)maxW / b.Width, (double)maxH / b.Height);
                Bitmap small = new Bitmap(Math.Max(1, (int)(b.Width * f)), Math.Max(1, (int)(b.Height * f)), PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(b, new Rectangle(0, 0, small.Width, small.Height));
                }
                b.Dispose();
                b = small;
            }
            return b;
        }

        static Bitmap ToGdi(BitmapSource src)
        {
            FormatConvertedBitmap conv = new FormatConvertedBitmap(src, WM.PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { conv.CopyPixels(System.Windows.Int32Rect.Empty, bd.Scan0, bd.Stride * h, bd.Stride); }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        // mode: 0 = as is, 1 = fill (crop the overflow), 2 = fit (pad), 3 = stretch
        public static Bitmap Resize(Bitmap src, int W, int H, int mode, Color pad)
        {
            Bitmap dst = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(dst))
            using (ImageAttributes ia = new ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                ia.SetWrapMode(WrapMode.TileFlipXY);
                if (mode == 3)
                {
                    g.DrawImage(src, new Rectangle(0, 0, W, H), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
                else if (mode == 1)
                {
                    double s = Math.Max((double)W / src.Width, (double)H / src.Height);
                    float sw = (float)(W / s), sh = (float)(H / s);
                    g.DrawImage(src, new Rectangle(0, 0, W, H), (src.Width - sw) / 2f, (src.Height - sh) / 2f, sw, sh, GraphicsUnit.Pixel, ia);
                }
                else
                {
                    g.Clear(pad);
                    double s = Math.Min((double)W / src.Width, (double)H / src.Height);
                    int dw = (int)Math.Round(src.Width * s), dh = (int)Math.Round(src.Height * s);
                    g.DrawImage(src, new Rectangle((W - dw) / 2, (H - dh) / 2, dw, dh), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return dst;
        }

        static Bitmap Flatten(Bitmap src, Color bg, PixelFormat pf)
        {
            Bitmap flat = new Bitmap(src.Width, src.Height, pf);
            using (Graphics g = Graphics.FromImage(flat))
            {
                g.Clear(bg);
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
            }
            return flat;
        }

        static byte[] ToBgra(Bitmap b)
        {
            BitmapData bd = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int row = b.Width * 4;
                byte[] buf = new byte[row * b.Height];
                for (int y = 0; y < b.Height; y++) Marshal.Copy(bd.Scan0 + y * bd.Stride, buf, y * row, row);
                return buf;
            }
            finally { b.UnlockBits(bd); }
        }

        static void SaveGdi(Bitmap bmp, string path, OutFormat f, int quality)
        {
            if (f.Ext == ".jpg")
            {
                using (Bitmap flat = Flatten(bmp, Color.White, PixelFormat.Format24bppRgb))
                {
                    ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                    using (EncoderParameters ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(1, Math.Min(100, quality)));
                        flat.Save(path, codec, ep);
                    }
                }
            }
            else if (f.Ext == ".bmp")
            {
                using (Bitmap flat = Flatten(bmp, Color.White, PixelFormat.Format24bppRgb)) flat.Save(path, ImageFormat.Bmp);
            }
            else bmp.Save(path, f.Gdi);
        }

        static bool IsAscii(string s) { return s.All(ch => ch < 128); }

        // %TEMP% contains the user name, which may be non-ASCII; fall back to C:\ProgramData then the system temp.
        static string AsciiTempDir()
        {
            string[] candidates = {
                Path.GetTempPath(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ImageConverter", "tmp"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
            foreach (string dir in candidates)
            {
                if (!IsAscii(dir)) continue;
                try
                {
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, "imgconv_probe_" + Guid.NewGuid().ToString("N"));
                    File.WriteAllBytes(probe, new byte[0]);
                    File.Delete(probe);
                    return dir;
                }
                catch { }
            }
            throw new Exception(Lang.T("Не найдена временная папка с латинским путём для внешнего кодировщика",
                                       "No temp folder with an ASCII path was found for the external encoder"));
        }

        // External encoders get ASCII temp paths (some don't handle Unicode paths); the result is moved into place.
        static void SaveExternal(Bitmap bmp, string path, OutFormat f, int quality)
        {
            string tag = "imgconv_" + Guid.NewGuid().ToString("N");
            string tmpDir = AsciiTempDir();
            string tmpIn = Path.Combine(tmpDir, tag + ".png");
            string tmpOut = Path.Combine(tmpDir, tag + f.Ext);
            try
            {
                bmp.Save(tmpIn, ImageFormat.Png);
                int q = Math.Max(1, Math.Min(100, quality));
                string args;
                switch (f.Ext)
                {
                    case ".webp": args = "-quiet -q " + q + " -m 6 -metadata none \"" + tmpIn + "\" -o \"" + tmpOut + "\""; break;
                    case ".avif": args = "-q " + q + " \"" + tmpIn + "\" \"" + tmpOut + "\""; break;
                    default: args = "\"" + tmpIn + "\" \"" + tmpOut + "\" -q " + q; break;   // cjxl
                }
                ProcessStartInfo psi = new ProcessStartInfo(f.ToolPath, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                    WorkingDirectory = OutFormat.ToolsDir
                };
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(tmpOut))
                        throw new Exception(f.Tool + Lang.T(" завершился с ошибкой: ", " failed: ") + err.Trim());
                }
                File.Move(tmpOut, path);
            }
            finally
            {
                try { File.Delete(tmpIn); } catch { }
                try { if (File.Exists(tmpOut)) File.Delete(tmpOut); } catch { }
            }
        }

        public static void Save(Bitmap bmp, string path, OutFormat f, int quality)
        {
            switch (f.Kind)
            {
                case EncKind.WinRt:
                    using (Bitmap flat = Flatten(bmp, Color.White, PixelFormat.Format32bppArgb))
                        WinRtImaging.Encode(f.EncoderId, ToBgra(flat), flat.Width, flat.Height, path, f.HasQuality ? quality : 0);
                    break;
                case EncKind.External: SaveExternal(bmp, path, f, quality); break;
                default: SaveGdi(bmp, path, f, quality); break;
            }
        }

        // Metadata never carries over: images are decoded to pixels and encoded from scratch, so EXIF (GPS, date,
        // camera) is not written to the output. Orientation is applied to the pixels on load.
        public static ConvertResult ConvertFile(string input, string output, OutFormat f, int quality, int mode, int W, int H, long maxBytes)
        {
            bool existed = File.Exists(output);
            try
            {
                using (Bitmap src = Load(input))
                {
                    // "fit" padding: transparent where the format keeps alpha, black otherwise
                    bool alpha = f.Ext == ".png" || f.Ext == ".tif" || f.Ext == ".webp" || f.Ext == ".avif" || f.Ext == ".jxl";
                    Bitmap resized = mode == 0 || W <= 0 || H <= 0 ? null : Resize(src, W, H, mode, alpha ? Color.Transparent : Color.Black);
                    try
                    {
                        Bitmap img = resized ?? src;
                        ConvertResult res = new ConvertResult { Quality = f.HasQuality ? quality : 0, Size = img.Size };
                        if (maxBytes > 0 && f.HasQuality) SaveUnder(img, output, f, quality, maxBytes, res);
                        else Save(img, output, f, quality);
                        return res;
                    }
                    finally { if (resized != null) resized.Dispose(); }
                }
            }
            catch
            {
                // don't leave a broken/empty file behind (only if we created it)
                if (!existed) { try { if (File.Exists(output)) File.Delete(output); } catch { } }
                throw;
            }
        }

        // ---- target file size

        const int MinTargetQuality = 35;     // below this, shrinking the picture looks better than more compression
        const int MaxDownscaleSteps = 10;    // 0.8^10 ≈ 11% of the width

        static long SizeAt(Bitmap img, OutFormat f, int q, string tmp)
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            Save(img, tmp, f, q);
            return new FileInfo(tmp).Length;
        }

        static void Replace(string from, string to)
        {
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }

        // Highest quality in [lo, hi] whose file fits; the fitting file is left at `best`. 0 if even lo is too big.
        static int FindQuality(Bitmap img, OutFormat f, int lo, int hi, long maxBytes, string tmp, string best)
        {
            if (SizeAt(img, f, hi, tmp) <= maxBytes) { Replace(tmp, best); return hi; }
            if (lo >= hi || SizeAt(img, f, lo, tmp) > maxBytes) return 0;
            Replace(tmp, best);
            int fits = lo, tooBig = hi;
            while (tooBig - fits > 1)
            {
                int mid = (fits + tooBig) / 2;
                if (SizeAt(img, f, mid, tmp) <= maxBytes) { fits = mid; Replace(tmp, best); }
                else tooBig = mid;
            }
            return fits;
        }

        // Binary search on quality (up to the user's setting, not below MinTargetQuality); if that can't fit,
        // shrink the picture by 20% steps and search again. If nothing fits, the smallest attempt is kept.
        static void SaveUnder(Bitmap img, string output, OutFormat f, int maxQuality, long maxBytes, ConvertResult res)
        {
            string tag = "imgconv_" + Guid.NewGuid().ToString("N");
            string dir = f.Kind == EncKind.External ? AsciiTempDir() : Path.GetTempPath();
            string tmp = Path.Combine(dir, tag + "_try" + f.Ext), best = Path.Combine(dir, tag + "_best" + f.Ext);
            int hi = Math.Max(1, Math.Min(100, maxQuality)), lo = Math.Min(MinTargetQuality, hi);
            Bitmap cur = img, scaled = null;
            try
            {
                for (int step = 0; ; step++)
                {
                    int q = FindQuality(cur, f, lo, hi, maxBytes, tmp, best);
                    if (q > 0)
                    {
                        Replace(best, output);
                        res.Quality = q; res.Size = cur.Size; res.Downscaled = step > 0;
                        return;
                    }
                    if (step >= MaxDownscaleSteps || cur.Width < 32 || cur.Height < 32) break;
                    int nw = Math.Max(1, (int)Math.Round(cur.Width * 0.8)), nh = Math.Max(1, (int)Math.Round(cur.Height * 0.8));
                    Bitmap next = Resize(cur, nw, nh, 3, Color.Black);
                    if (scaled != null) scaled.Dispose();
                    scaled = next; cur = next;
                }
                // could not fit: keep the smallest attempt and say so
                Save(cur, output, f, lo);
                res.Quality = lo; res.Size = cur.Size; res.Downscaled = cur != img; res.MissedTarget = true;
            }
            finally
            {
                if (scaled != null) scaled.Dispose();
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                try { if (File.Exists(best)) File.Delete(best); } catch { }
            }
        }

        // Never overwrites: "name.png", then "name (2).png", "name (3).png", ...
        public static string MakeOutputPath(string input, string outDir, OutFormat f, string suffix)
        {
            string dir = string.IsNullOrEmpty(outDir) ? Path.GetDirectoryName(input) : outDir;
            string baseName = Path.GetFileNameWithoutExtension(input) + suffix;
            string path = Path.Combine(dir, baseName + f.Ext);
            for (int n = 2; File.Exists(path) || string.Equals(path, input, StringComparison.OrdinalIgnoreCase); n++)
                path = Path.Combine(dir, baseName + " (" + n + ")" + f.Ext);
            return path;
        }
    }

}
