using System.Runtime.InteropServices;
using SkiaSharp;

namespace XiangqiCore.Recognition;

public static class ImageOps
{
    public const int DetSize = 640;
    public const int ClsSize = 64;
    private const int PrecisionBits = 22;

    /// <summary>PIL BILINEAR-equivalent resize on RGBA byte buffers (RGB only, alpha=255).</summary>
    public static byte[] PilResizeRgb(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        var hk = ComputeCoeffs(sw, dw);
        var vk = ComputeCoeffs(sh, dh);

        // horizontal pass -> temp (dw x sh)
        var temp = new byte[dw * sh * 4];
        for (var y = 0; y < sh; y++)
        {
            var srcRow = y * sw * 4;
            var dstRow = y * dw * 4;
            for (var xx = 0; xx < dw; xx++)
            {
                var xmin = hk.Bounds[xx * 2];
                var count = hk.Bounds[xx * 2 + 1];
                long s0 = 1 << (PrecisionBits - 1);
                long s1 = 1 << (PrecisionBits - 1);
                long s2 = 1 << (PrecisionBits - 1);
                for (var k = 0; k < count; k++)
                {
                    var w = hk.Coeffs[xx * hk.Ksize + k];
                    var si = srcRow + (xmin + k) * 4;
                    s0 += src[si] * w;
                    s1 += src[si + 1] * w;
                    s2 += src[si + 2] * w;
                }
                var di = dstRow + xx * 4;
                temp[di] = Clip8(s0);
                temp[di + 1] = Clip8(s1);
                temp[di + 2] = Clip8(s2);
                temp[di + 3] = 255;
            }
        }

        // vertical pass -> dst (dw x dh)
        for (var yy = 0; yy < dh; yy++)
        {
            var ymin = vk.Bounds[yy * 2];
            var count = vk.Bounds[yy * 2 + 1];
            for (var xx = 0; xx < dw; xx++)
            {
                long s0 = 1 << (PrecisionBits - 1);
                long s1 = 1 << (PrecisionBits - 1);
                long s2 = 1 << (PrecisionBits - 1);
                for (var k = 0; k < count; k++)
                {
                    var w = vk.Coeffs[yy * vk.Ksize + k];
                    var si = ((ymin + k) * dw + xx) * 4;
                    s0 += temp[si] * w;
                    s1 += temp[si + 1] * w;
                    s2 += temp[si + 2] * w;
                }
                var di = (yy * dw + xx) * 4;
                dst[di] = Clip8(s0);
                dst[di + 1] = Clip8(s1);
                dst[di + 2] = Clip8(s2);
                dst[di + 3] = 255;
            }
        }
        return dst;
    }

    private static (int[] Bounds, long[] Coeffs, int Ksize) ComputeCoeffs(int inSize, int outSize)
    {
        var scale = (double)inSize / outSize;
        var filterScale = Math.Max(scale, 1.0);
        var support = filterScale; // bilinear support = 1.0
        var ksize = (int)Math.Ceiling(support) * 2 + 1;
        var bounds = new int[outSize * 2];
        var coeffs = new long[outSize * ksize];
        for (var xx = 0; xx < outSize; xx++)
        {
            var center = (xx + 0.5) * scale;
            var xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            var xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;
            double sum = 0;
            var ws = new double[xmax];
            for (var k = 0; k < xmax; k++)
            {
                var d = (k + xmin - center + 0.5) / filterScale;
                var w = Math.Max(0.0, 1.0 - Math.Abs(d));
                ws[k] = w;
                sum += w;
            }
            if (sum != 0.0)
                for (var k = 0; k < xmax; k++)
                    ws[k] /= sum;
            for (var k = 0; k < xmax; k++)
                coeffs[xx * ksize + k] = (long)(0.5 + ws[k] * (1L << PrecisionBits));
            bounds[xx * 2] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        return (bounds, coeffs, ksize);
    }

    private static byte Clip8(long v)
    {
        var x = (int)(v >> PrecisionBits);
        return (byte)Math.Clamp(x, 0, 255);
    }

    /// <summary>Read bitmap pixels as RGBA8888 bytes.</summary>
    public static byte[] GetRgbaBytes(SKBitmap bmp)
    {
        using var copy = bmp.Copy(SKColorType.Rgba8888);
        using var pixmap = copy.PeekPixels();
        var bytes = new byte[bmp.Width * bmp.Height * 4];
        Marshal.Copy(pixmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }

    /// <summary>Letterbox to square, return normalized RGB CHW float tensor + scale/offsets.</summary>
    public static (float[] Tensor, float Scale, int OffX, int OffY) LetterboxRgbChw(SKBitmap img, int size)
    {
        var src = GetRgbaBytes(img);
        var sw = img.Width;
        var sh = img.Height;
        var scale = Math.Min((float)size / img.Width, (float)size / img.Height);
        var nw = Math.Max(1, (int)Math.Round(img.Width * scale));
        var nh = Math.Max(1, (int)Math.Round(img.Height * scale));
        var offx = (size - nw) / 2;
        var offy = (size - nh) / 2;

        var resized = PilResizeRgb(src, sw, sh, nw, nh);
        var tensor = new float[3 * size * size];
        // fill padding (114,114,114)
        for (var i = 0; i < size * size; i++)
        {
            tensor[i] = 114f / 255f;
            tensor[size * size + i] = 114f / 255f;
            tensor[2 * size * size + i] = 114f / 255f;
        }

        for (var y = 0; y < nh; y++)
        {
            for (var x = 0; x < nw; x++)
            {
                var si = (y * nw + x) * 4;
                var di = ((offy + y) * size + (offx + x));
                tensor[di] = resized[si] / 255f;
                tensor[size * size + di] = resized[si + 1] / 255f;
                tensor[2 * size * size + di] = resized[si + 2] / 255f;
            }
        }
        return (tensor, scale, offx, offy);
    }

    /// <summary>Convert an arbitrary SKBitmap into a normalized RGBA byte buffer.</summary>
    public static (byte[] Rgba, int Width, int Height) ToRgba(SKBitmap img)
    {
        var bytes = GetRgbaBytes(img);
        return (bytes, img.Width, img.Height);
    }

    public static SKBitmap Crop(SKBitmap img, int x1, int y1, int x2, int y2)
    {
        // 限制在图像内并保证 0<=x1<x2<=Width（越界时 Subset 会返回 null 导致 FromImage 抛异常）
        x1 = Math.Clamp(x1, 0, img.Width - 1);
        y1 = Math.Clamp(y1, 0, img.Height - 1);
        x2 = Math.Clamp(x2, 1, img.Width);
        y2 = Math.Clamp(y2, 1, img.Height);
        if (x2 <= x1) x2 = x1 + 1;
        if (y2 <= y1) y2 = y1 + 1;
        using var image = SKImage.FromBitmap(img);
        using var subset = image.Subset(new SKRectI(x1, y1, x2, y2));
        return SKBitmap.FromImage(subset);
    }

    public static float[] CropResizeRgbChw(SKBitmap img, int x1, int y1, int x2, int y2, int size)
    {
        var src = GetRgbaBytes(img);
        var w = x2 - x1;
        var h = y2 - y1;
        var crop = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            Array.Copy(src, ((y1 + y) * img.Width + x1) * 4, crop, y * w * 4, w * 4);
        var resized = PilResizeRgb(crop, w, h, size, size);
        var tensor = new float[3 * size * size];
        for (var i = 0; i < size * size; i++)
        {
            var si = i * 4;
            tensor[i] = resized[si] / 255f;
            tensor[size * size + i] = resized[si + 1] / 255f;
            tensor[2 * size * size + i] = resized[si + 2] / 255f;
        }
        return tensor;
    }
}
