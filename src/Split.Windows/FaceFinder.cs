using global::Windows.Graphics.Imaging;
using global::Windows.Media.FaceAnalysis;
using global::Windows.Storage.Streams;

namespace Split.Windows;

/// <summary>Faces in a picture, found on this PC by the system's own detector (nothing leaves the machine).</summary>
internal static class FaceFinder
{
    public static IReadOnlyList<PixelBox> Find(byte[] gray, int width, int height)
    {
        // The detector's calls are asynchronous: they run on a pool thread, so waiting here can't block on a window's thread.
        return Task.Run(async () =>
        {
            var detector = await FaceDetector.CreateAsync();
            if (!FaceDetector.GetSupportedBitmapPixelFormats().Contains(BitmapPixelFormat.Gray8)) return (IReadOnlyList<PixelBox>)[];
            var writer = new DataWriter();
            writer.WriteBytes(gray);
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Gray8, width, height);
            var faces = await detector.DetectFacesAsync(bitmap);
            return faces.Select(f => new PixelBox((int)f.FaceBox.X, (int)f.FaceBox.Y, (int)f.FaceBox.Width, (int)f.FaceBox.Height)).ToList();
        }).GetAwaiter().GetResult();
    }
}
