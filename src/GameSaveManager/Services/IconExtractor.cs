using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace GameSaveManager.Services;

/// <summary>
/// 从 exe 提取高分辨率图标（256px jumbo → 32px 兜底），保存为 PNG。
/// </summary>
public static class IconExtractor
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    [DllImport("comctl32.dll")]
    private static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const int SHIL_JUMBO = 0x4;
    private static readonly Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    /// <summary>提取图标并保存 PNG；成功返回文件路径，失败返回 null。</summary>
    public static string? ExtractToPng(string exePath, string saveTo)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            hIcon = GetJumboIcon(exePath);
            if (hIcon == IntPtr.Zero)
                hIcon = GetLargeIcon(exePath);
            if (hIcon == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            // Windows jumbo 图标坑：exe 无 256px 图标时，系统把 48px 小图标画在
            // 256x256 透明画布左上角返回。裁掉透明边缘得到实际内容尺寸。
            var trimmed = TrimTransparent(source);
            // 卡片式图标（如大富翁11）自带不透明浅画框+外阴影，UI 再套圆角边框会变成"框中框"，
            // 保守地识别并裁掉自带画框；透明背景 logo / 满版纯色 / 深色海报不动。
            trimmed = TrimNeutralBorder(trimmed);

            Directory.CreateDirectory(Path.GetDirectoryName(saveTo)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(trimmed));
            using var fs = File.Create(saveTo);
            encoder.Save(fs);
            return saveTo;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
        }
    }

    /// <summary>裁掉四周完全透明的边缘（无透明空白或全透明时原样返回）。</summary>
    private static BitmapSource TrimTransparent(BitmapSource src)
    {
        try
        {
            var bgra = new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    if (px[row + x * 4 + 3] > 8)   // 非透明像素（含少量容差）
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0) return src;   // 全透明
            if (minX == 0 && minY == 0 && maxX == w - 1 && maxY == h - 1) return src;   // 无空白边
            var cropped = new CroppedBitmap(bgra, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
            cropped.Freeze();
            return cropped;
        }
        catch { return src; }
    }

    /// <summary>
    /// 二次裁剪：识别"卡片式图标"自带的不透明浅色画框（外缘常带一圈投影/透明边），把框和外阴影一起裁掉。
    /// 判据极保守，四边逐行向内扫描，每行分为：F=实体浅框（亮、低饱和、不透明）/ S=投影或透明 / A=画面。
    /// 接受条件：① 四边都存在 S*→中性带、且带内确实进入画面（找到明确内边界，厚度 3px~短边15%）；
    /// ② 至少 2 条边见到实体浅框行（纯透明背景的 logo 四条边都不会有 F，借此排除）；
    /// ③ 四边厚度均衡（最大≤最小×4）。因此透明背景 logo（巫师狼头）、彩色满版（植物大战僵尸蓝底）、
    /// 深色海报（艾尔登法环）均不会被误裁。判据已用本机 156 个真实 exe 图标全量回归，仅命中带框卡片 1 例。
    /// </summary>
    internal static BitmapSource TrimNeutralBorder(BitmapSource src)
    {
        try
        {
            var bgra = src.Format == System.Windows.Media.PixelFormats.Bgra32
                ? src
                : new FormatConvertedBitmap(src, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            if (w < 32 || h < 32) return src;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);

            int minDim = Math.Min(w, h);
            int cap = (int)Math.Round(minDim * 0.15);   // 单边最多裁 15%
            int maxOuter = (int)Math.Round(minDim * 0.12);

            // 2 = 实体浅框，1 = 投影/透明，0 = 画面
            static int Kind(byte[] p, int i)
            {
                byte b = p[i], g = p[i + 1], r = p[i + 2], a = p[i + 3];
                if (a < 160) return 1;
                int bright = (r + g + b) / 3;
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (a < 235 && bright < 80) return 1;
                if (bright >= 168 && mx - mn <= 55) return 2;
                return 0;
            }
            // 一条扫描行的分类：F 实体浅框行 / S 投影透明行 / A 画面行（中性像素不足85%或亮度过杂）
            static char LineClass(byte[] p, int n, Func<int, int> at)
            {
                int frame = 0, neutral = 0, sum = 0;
                var brights = new int[n];
                for (int k = 0; k < n; k++)
                {
                    int kind = Kind(p, at(k));
                    int i = at(k);
                    int br = (p[i + 2] + p[i + 1] + p[i]) / 3;
                    brights[k] = br;
                    sum += br;
                    if (kind == 2) frame++;
                    if (kind != 0) neutral++;
                }
                if (neutral < n * 0.85) return 'A';
                double mean = (double)sum / n, acc = 0;
                foreach (int v in brights) acc += (v - mean) * (v - mean);
                if (Math.Sqrt(acc / n) > 55) return 'A';
                return frame >= n * 0.45 ? 'F' : 'S';
            }
            // 从外缘向内走：S*(至多 maxOuter) → 中性带(F/S)，遇画面停止。返回厚度（无效为-1）与是否见过实体框
            static int Walk(Func<int, char> line, int cap, int maxOuter, out bool sawFrame)
            {
                int t = 0, frames = 0;
                bool inFrame = false;
                while (t < cap)
                {
                    char c = line(t);
                    if (c == 'A') break;
                    if (!inFrame && c == 'S')
                    {
                        if (t >= maxOuter) break;
                    }
                    else
                    {
                        inFrame = true;
                        if (c == 'F') frames++;
                    }
                    t++;
                }
                sawFrame = frames >= 2;
                bool hitArt = t < cap && line(t) == 'A';   // 必须走到明确内边界，防止裁进无边图
                return t >= 3 && hitArt ? t : -1;
            }

            int stride = w * 4;
            int top    = Walk(k => LineClass(px, w, x => k * stride + x * 4), cap, maxOuter, out bool fTop);
            int bottom = Walk(k => LineClass(px, w, x => (h - 1 - k) * stride + x * 4), cap, maxOuter, out bool fBottom);
            int left   = Walk(k => LineClass(px, h, y => y * stride + k * 4), cap, maxOuter, out bool fLeft);
            int right  = Walk(k => LineClass(px, h, y => y * stride + (w - 1 - k) * 4), cap, maxOuter, out bool fRight);
            if (top < 0 || bottom < 0 || left < 0 || right < 0) return src;
            int frameSides = (fTop ? 1 : 0) + (fBottom ? 1 : 0) + (fLeft ? 1 : 0) + (fRight ? 1 : 0);
            if (frameSides < 2) return src;
            int thMin = Math.Min(top, Math.Min(bottom, Math.Min(left, right)));
            int thMax = Math.Max(top, Math.Max(bottom, Math.Max(left, right)));
            if (thMax > thMin * 4.0) return src;   // 四边厚度失衡：不是规则画框

            int cw = w - left - right, ch = h - top - bottom;
            if (cw >= w || ch >= h || cw <= 0 || ch <= 0) return src;
            var result = new CroppedBitmap(bgra, new Int32Rect(left, top, cw, ch));
            result.Freeze();
            return result;
        }
        catch { return src; }
    }

    /// <summary>对已落盘的 PNG 图标重跑两轮裁剪（透明边 + 自带画框）；确有裁切才原地覆写，返回是否改动。
    /// 用于新版本启动时迁移旧缓存；判定幂等，已裁过的图再次扫描不会变化。</summary>
    internal static bool TrimPngFile(string path)
    {
        try
        {
            BitmapSource bmp;
            using (var fs = File.OpenRead(path))
            {
                var dec = new PngBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                bmp = dec.Frames[0];
                bmp.Freeze();
            }
            var t1 = TrimTransparent(bmp);
            var t2 = TrimNeutralBorder(t1);
            if (t2.PixelWidth == bmp.PixelWidth && t2.PixelHeight == bmp.PixelHeight) return false;

            string tmp = path + ".trimming.tmp";
            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(t2));
                using var outFs = File.Create(tmp);
                encoder.Save(outFs);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                return false;
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch { return false; }
    }

    private static IntPtr GetJumboIcon(string exePath)
    {
        try
        {
            var sfi = new SHFILEINFO();
            uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
            IntPtr res = SHGetFileInfo(exePath, 0, ref sfi, size, SHGFI_SYSICONINDEX);
            if (res == IntPtr.Zero || sfi.iIcon < 0) return IntPtr.Zero;

            IntPtr hImgList = IntPtr.Zero;
            Guid iid = IID_IImageList;
            int hr = SHGetImageList(SHIL_JUMBO, ref iid, out hImgList);
            if (hr != 0 || hImgList == IntPtr.Zero) return IntPtr.Zero;

            IntPtr hIcon = ImageList_GetIcon(hImgList, sfi.iIcon, 0);
            return hIcon; // 由调用方 DestroyIcon
        }
        catch { return IntPtr.Zero; }
    }

    private static IntPtr GetLargeIcon(string exePath)
    {
        try
        {
            var sfi = new SHFILEINFO();
            uint size = (uint)Marshal.SizeOf<SHFILEINFO>();
            IntPtr res = SHGetFileInfo(exePath, 0, ref sfi, size, SHGFI_ICON | SHGFI_LARGEICON);
            if (res == IntPtr.Zero) return IntPtr.Zero;
            IntPtr h = sfi.hIcon;
            sfi.hIcon = IntPtr.Zero;
            return h;
        }
        catch { return IntPtr.Zero; }
    }
}
