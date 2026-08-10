using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace FluxConnect.Desktop.Core.Capture;

/// <summary>
/// BGRA ham piksel verisini UI thread dışında JPEG'e dönüştürür.
/// </summary>
public static class JpegEncoder
{
    private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders()
        .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static byte[] EncodeBgra32(ReadOnlySpan<byte> rawData, int width, int height, int rowPitch, int quality)
    {
        quality = Math.Clamp(quality, StreamQualityController.MinQuality, StreamQualityController.MaxQuality);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var bmpData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            var stride = bmpData.Stride;
            var dest = bmpData.Scan0;
            if (stride == rowPitch)
            {
                Marshal.Copy(rawData.Slice(0, rowPitch * height).ToArray(), 0, dest, rowPitch * height);
            }
            else
            {
                for (int y = 0; y < height; y++)
                {
                    var srcOffset = y * rowPitch;
                    var dstOffset = dest + y * stride;
                    Marshal.Copy(rawData.Slice(srcOffset, width * 4).ToArray(), 0, dstOffset, width * 4);
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }

        using var ms = new MemoryStream();
        if (JpegCodec != null)
        {
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(ms, JpegCodec, encoderParams);
        }
        else
        {
            bitmap.Save(ms, ImageFormat.Jpeg);
        }

        return ms.ToArray();
    }
}
