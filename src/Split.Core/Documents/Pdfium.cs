using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;

namespace Split;

/// <summary>One character of a page as PDFium reports it.</summary>
public readonly record struct PdfChar(char Character, double Size, bool Bold, bool Italic, string Font, string? Color, double X, double Y);

public sealed class PdfPasswordException : Exception;

public sealed class PdfDamagedException : Exception;

/// <summary>
/// A PDF opened with PDFium (the binaries that come with the app, nothing is fetched). The library is not thread-safe,
/// so every call goes through one lock. Page numbers start at 0.
/// </summary>
public sealed class PdfiumDocument : IDisposable
{
    private static readonly object Gate = new();
    private static bool started;

    private IntPtr handle;
    private GCHandle pinned;

    private PdfiumDocument() { }

    /// <exception cref="PdfPasswordException">The file needs a password to open.</exception>
    /// <exception cref="PdfDamagedException">The file is not a PDF PDFium can read.</exception>
    public static PdfiumDocument Open(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var document = new PdfiumDocument { pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned) };
        lock (Gate)
        {
            if (!started)
            {
                Native.FPDF_InitLibrary();
                started = true;
            }
            document.handle = Native.FPDF_LoadMemDocument64(document.pinned.AddrOfPinnedObject(), (UIntPtr)bytes.Length, null);
            if (document.handle == IntPtr.Zero)
            {
                var error = Native.FPDF_GetLastError();
                document.Dispose();
                // 4 is "password required or wrong".
                throw error == 4 ? new PdfPasswordException() : new PdfDamagedException();
            }
        }
        return document;
    }

    public int PageCount
    {
        get
        {
            lock (Gate) return Native.FPDF_GetPageCount(handle);
        }
    }

    /// <summary>The page's size in points, rotation applied.</summary>
    public (double Width, double Height) PageSize(int index) => WithPage(index, page => ((double)Native.FPDF_GetPageWidthF(page), (double)Native.FPDF_GetPageHeightF(page)));

    private T WithPage<T>(int index, Func<IntPtr, T> body)
    {
        lock (Gate)
        {
            var page = Native.FPDF_LoadPage(handle, index);
            if (page == IntPtr.Zero) throw new PdfDamagedException();
            try { return body(page); }
            finally { Native.FPDF_ClosePage(page); }
        }
    }

    /// <summary>The page as pixels on white, <paramref name="scale"/> pixels per point (annotations drawn, rotation applied).</summary>
    public IMagickImage<ushort>? Render(int index, double scale)
    {
        Imaging.Initialize();
        return WithPage(index, page =>
        {
            var width = (int)Math.Round(Native.FPDF_GetPageWidthF(page) * scale);
            var height = (int)Math.Round(Native.FPDF_GetPageHeightF(page) * scale);
            // A page cannot be drawn with no pixels, or with more than a bitmap can take.
            if (width < 1 || height < 1 || (long)width * height > 200_000_000) return null;
            var bitmap = Native.FPDFBitmap_Create(width, height, 0);
            if (bitmap == IntPtr.Zero) return null;
            try
            {
                Native.FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                Native.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0x01);
                var stride = Native.FPDFBitmap_GetStride(bitmap);
                var buffer = Native.FPDFBitmap_GetBuffer(bitmap);
                var row = new byte[stride];
                var rgb = new byte[width * height * 3];
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(buffer + y * stride, row, 0, stride);
                    for (var x = 0; x < width; x++)
                        (rgb[(y * width + x) * 3], rgb[(y * width + x) * 3 + 1], rgb[(y * width + x) * 3 + 2]) = (row[x * 4], row[x * 4 + 1], row[x * 4 + 2]);
                }
                return (IMagickImage<ushort>)new MagickImage(rgb, new PixelReadSettings((uint)width, (uint)height, StorageType.Char, "BGR"));
            }
            finally { Native.FPDFBitmap_Destroy(bitmap); }
        });
    }

    /// <summary>The characters of the page in reading order, each with its font, size and weight; line ends come as \r or \n.</summary>
    public List<PdfChar> Characters(int index) => WithPage(index, page =>
    {
        var characters = new List<PdfChar>();
        var text = Native.FPDFText_LoadPage(page);
        if (text == IntPtr.Zero) return characters;
        try
        {
            var count = Native.FPDFText_CountChars(text);
            var name = new byte[128];
            for (var i = 0; i < count; i++)
            {
                var code = Native.FPDFText_GetUnicode(text, i);
                if (code == 0) continue;
                Native.FPDFText_GetCharOrigin(text, i, out var x, out var y);
                var length = Native.FPDFText_GetFontInfo(text, i, name, (uint)name.Length, out var flags);
                var font = length > 1 && length <= name.Length ? Encoding.ASCII.GetString(name, 0, (int)length - 1) : "";
                var weight = Native.FPDFText_GetFontWeight(text, i);
                // 0x40 is the italic bit of the font's flags; names carry the same news when flags are missing.
                var italic = (flags & 0x40) != 0 || font.Contains("Italic", StringComparison.OrdinalIgnoreCase) || font.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
                var bold = weight >= 600 || font.Contains("Bold", StringComparison.OrdinalIgnoreCase) || font.Contains("Black", StringComparison.OrdinalIgnoreCase);
                string? color = null;
                if (Native.FPDFText_GetFillColor(text, i, out var r, out var g, out var b, out _) != 0 && (r, g, b) != (0, 0, 0)) color = $"{r:X2}{g:X2}{b:X2}";
                characters.Add(new PdfChar((char)code, Native.FPDFText_GetFontSize(text, i), bold, italic, font, color, x, y));
            }
        }
        finally { Native.FPDFText_ClosePage(text); }
        return characters;
    });

    public void Dispose()
    {
        lock (Gate)
        {
            if (handle != IntPtr.Zero) Native.FPDF_CloseDocument(handle);
            handle = IntPtr.Zero;
        }
        if (pinned.IsAllocated) pinned.Free();
    }

    private static class Native
    {
        private const string Library = "pdfium";

        [DllImport(Library)] public static extern void FPDF_InitLibrary();
        [DllImport(Library)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, UIntPtr size, [MarshalAs(UnmanagedType.LPStr)] string? password);
        [DllImport(Library)] public static extern uint FPDF_GetLastError();
        [DllImport(Library)] public static extern void FPDF_CloseDocument(IntPtr document);
        [DllImport(Library)] public static extern int FPDF_GetPageCount(IntPtr document);
        [DllImport(Library)] public static extern IntPtr FPDF_LoadPage(IntPtr document, int index);
        [DllImport(Library)] public static extern void FPDF_ClosePage(IntPtr page);
        [DllImport(Library)] public static extern float FPDF_GetPageWidthF(IntPtr page);
        [DllImport(Library)] public static extern float FPDF_GetPageHeightF(IntPtr page);
        [DllImport(Library)] public static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
        [DllImport(Library)] public static extern void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
        [DllImport(Library)] public static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
        [DllImport(Library)] public static extern int FPDFBitmap_GetStride(IntPtr bitmap);
        [DllImport(Library)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
        [DllImport(Library)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
        [DllImport(Library)] public static extern IntPtr FPDFText_LoadPage(IntPtr page);
        [DllImport(Library)] public static extern void FPDFText_ClosePage(IntPtr text);
        [DllImport(Library)] public static extern int FPDFText_CountChars(IntPtr text);
        [DllImport(Library)] public static extern uint FPDFText_GetUnicode(IntPtr text, int index);
        [DllImport(Library)] public static extern double FPDFText_GetFontSize(IntPtr text, int index);
        [DllImport(Library)] public static extern int FPDFText_GetFontWeight(IntPtr text, int index);
        [DllImport(Library)] public static extern uint FPDFText_GetFontInfo(IntPtr text, int index, byte[] buffer, uint length, out int flags);
        [DllImport(Library)] public static extern int FPDFText_GetFillColor(IntPtr text, int index, out uint r, out uint g, out uint b, out uint a);
        [DllImport(Library)] public static extern int FPDFText_GetCharOrigin(IntPtr text, int index, out double x, out double y);
    }
}
