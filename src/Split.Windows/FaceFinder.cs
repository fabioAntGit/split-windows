using global::Windows.Graphics.Imaging;
using global::Windows.Media.FaceAnalysis;
using global::Windows.Storage.Streams;

namespace Split.Windows;

/// <summary>Faces in a picture, found on this PC by the system's own detector (nothing leaves the machine).</summary>
internal static class FaceFinder
{
    private static readonly object Gate = new();
    private static FaceDetector? detector;

    public static IReadOnlyList<PixelBox> Find(byte[] gray, int width, int height)
    {
        // The detector's calls are asynchronous: they run on a pool thread, so waiting here can't block on a window's thread.
        // One detector serves every call, and one call runs at a time: it is not built for being used at once.
        lock (Gate)
        {
            return Task.Run(async () =>
            {
                detector ??= await FaceDetector.CreateAsync();
                var writer = new DataWriter();
                writer.WriteBytes(gray);
                using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Gray8, width, height);
                var faces = await detector.DetectFacesAsync(bitmap);
                return (IReadOnlyList<PixelBox>)faces.Select(f => new PixelBox((int)f.FaceBox.X, (int)f.FaceBox.Y, (int)f.FaceBox.Width, (int)f.FaceBox.Height)).ToList();
            }).GetAwaiter().GetResult();
        }
    }
}
