using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Split.App;

/// <summary>
/// Files dragged out of the app the way File Explorer drags them: in the shell's own data object, with a
/// picture of their icons stacked under the pointer, which Explorer and the other apps that show drag
/// pictures draw as the drag goes.
/// </summary>
public static class ShellDrag
{
    /// <summary>Each icon's size, and how far each of the first five sits below and right of the one before.</summary>
    public const double IconSize = 32, Step = 3;
    public const int Steps = 4;

    public static System.Windows.DataObject Data(IReadOnlyList<string> paths, double scale)
    {
        SHCreateDataObject(IntPtr.Zero, 0, IntPtr.Zero, null, typeof(ComDataObject).GUID, out var data);
        SetFiles(data, paths);
        SetPicture(data, Picture(paths, scale), scale);
        return new System.Windows.DataObject(data);
    }

    /// <summary>The first file's icon on top, the next ones peeking out below and to the right.</summary>
    public static BitmapSource Picture(IReadOnlyList<string> paths, double scale)
    {
        var shown = Math.Min(paths.Count, Steps + 1);
        var side = (int)Math.Ceiling((IconSize + Step * Math.Max(0, shown - 1)) * scale);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            for (var index = shown - 1; index >= 0; index--)
                if (FileIcons.Of(paths[index]) is { } icon)
                    context.DrawImage(icon, new Rect(Step * index * scale, Step * index * scale, IconSize * scale, IconSize * scale));
        }
        var bitmap = new RenderTargetBitmap(side, side, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The names as a CF_HDROP, which is how every Windows app reads dropped files.</summary>
    private static void SetFiles(ComDataObject data, IReadOnlyList<string> paths)
    {
        var names = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var global = Marshal.AllocHGlobal(DropFilesSize + names.Length);
        // DROPFILES: where the names start, a drop point and a non-client flag nobody reads, and that the names are UTF-16.
        Marshal.WriteInt32(global, 0, DropFilesSize);
        Marshal.WriteInt64(global, 4, 0);
        Marshal.WriteInt32(global, 12, 0);
        Marshal.WriteInt32(global, 16, 1);
        Marshal.Copy(names, 0, global + DropFilesSize, names.Length);
        var format = new FORMATETC { cfFormat = CF_HDROP, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = global };
        try { data.SetData(ref format, ref medium, true); }
        catch
        {
            Marshal.FreeHGlobal(global);
            throw;
        }
    }

    /// <summary>Hands the picture to the shell, held at the middle of the first icon.</summary>
    private static void SetPicture(ComDataObject data, BitmapSource picture, double scale)
    {
        var width = picture.PixelWidth;
        var height = picture.PixelHeight;
        var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
        var bitmap = CreateDIBSection(IntPtr.Zero, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero) return;
        // Premultiplied BGRA, top row first: what the shell expects of a 32-bit drag picture.
        picture.CopyPixels(Int32Rect.Empty, bits, width * height * 4, width * 4);
        var image = new DragImage
        {
            Width = width, Height = height,
            OffsetX = (int)(IconSize / 2 * scale), OffsetY = (int)(IconSize / 2 * scale),
            Bitmap = bitmap, ColorKey = unchecked((int)0xFFFFFFFF),
        };
        try
        {
            // The shell takes the bitmap over once it has it.
            ((IDragSourceHelper)new DragDropHelper()).InitializeFromBitmap(ref image, data);
        }
        catch (COMException)
        {
            // Without its picture it is still a drag of the files.
            DeleteObject(bitmap);
        }
    }

    private const short CF_HDROP = 15;
    private const int DropFilesSize = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct DragImage
    {
        public int Width, Height, OffsetX, OffsetY;
        public IntPtr Bitmap;
        public int ColorKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [ComImport, Guid("4657278A-411B-11D2-839A-00C04FD918D0")]
    private class DragDropHelper;

    [ComImport, Guid("DE5BF786-477A-11D2-839D-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDragSourceHelper
    {
        void InitializeFromBitmap(ref DragImage image, ComDataObject data);
        void InitializeFromWindow(IntPtr window, IntPtr point, ComDataObject data);
    }

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SHCreateDataObject(IntPtr folder, uint count, IntPtr items, ComDataObject? inner, in Guid riid,
                                                  [MarshalAs(UnmanagedType.Interface)] out ComDataObject data);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
