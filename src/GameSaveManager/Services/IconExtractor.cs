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
