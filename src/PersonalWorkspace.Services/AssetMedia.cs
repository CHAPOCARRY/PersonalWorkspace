using System.Buffers.Binary;
using PersonalWorkspace.Core;

namespace PersonalWorkspace.Services;

// Bounded structural metadata inspection, not image rendering or general document parsing.
internal static class AssetMedia
{
    public static AssetFileInfo Inspect(Stream file, string extension, long length, string hash)
    {
        Span<byte> signature = stackalloc byte[12]; var got = file.Read(signature); file.Position = 0;
        var png = got >= 8 && signature[..8].SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 });
        var jpeg = got >= 3 && signature[0] == 255 && signature[1] == 216 && signature[2] == 255;
        extension = extension.ToLowerInvariant();
        if (extension is ".png" && !png || extension is ".jpg" or ".jpeg" && !jpeg) throw InvalidImage();
        if (png) { var (w,h) = Png(file); return new(length, hash, "image/png", w, h); }
        if (jpeg) { var (w,h) = Jpeg(file); return new(length, hash, "image/jpeg", w, h); }
        var zip = got >= 4 && signature[..4].SequenceEqual(new byte[] {80,75,3,4});
        var media = got >= 5 && signature[..5].SequenceEqual("%PDF-"u8) ? "application/pdf" : zip ? extension switch
        {
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/zip"
        } : extension == ".txt" ? "text/plain" : "application/octet-stream";
        return new(length, hash, media);
    }
    private static ContentValidationException InvalidImage() => new("The image content is invalid or does not match its PNG/JPEG filename.");
    private static (int, int) Png(Stream s)
    {
        s.Position = 8; var header = new byte[8]; var data = new byte[13]; int width = 0, height = 0; bool imageData = false; var chunks = 0;
        while (s.Position + 12 <= s.Length && ++chunks <= 1000000)
        {
            s.ReadExactly(header); var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0,4)); var kind = System.Text.Encoding.ASCII.GetString(header,4,4);
            if (size > int.MaxValue || s.Position + size + 4 > s.Length) throw InvalidImage();
            if (chunks == 1)
            {
                if (kind != "IHDR" || size != 13) throw InvalidImage(); s.ReadExactly(data);
                width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0,4)); height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4,4));
                var validDepth = data[9] switch { 0 => data[8] is 1 or 2 or 4 or 8 or 16, 2 or 4 or 6 => data[8] is 8 or 16, 3 => data[8] is 1 or 2 or 4 or 8, _ => false };
                if (width <= 0 || height <= 0 || !validDepth || data[10] != 0 || data[11] != 0 || data[12] > 1) throw InvalidImage();
            }
            else { if (kind == "IHDR") throw InvalidImage(); s.Seek(size, SeekOrigin.Current); }
            s.Seek(4, SeekOrigin.Current); // CRC verification/full pixel decoding belongs to the native image consumer.
            if (kind == "IDAT" && size > 0) imageData = true;
            if (kind == "IEND") { if (size != 0 || !imageData || s.Position != s.Length) throw InvalidImage(); return (width,height); }
        }
        throw InvalidImage();
    }
    private static (int, int) Jpeg(Stream s)
    {
        s.Position = s.Length - 2; if (s.ReadByte() != 255 || s.ReadByte() != 217) throw InvalidImage();
        s.Position = 2; int width = 0, height = 0; var header = new byte[7]; var segments = 0;
        while (s.Position + 4 < s.Length && ++segments < 1000000)
        {
            if (s.ReadByte() != 255) throw InvalidImage(); int marker; do { marker = s.ReadByte(); } while (marker == 255);
            if (marker is 0 or 216 or 217 or < 0) throw InvalidImage();
            if (marker == 1 || marker is >= 208 and <= 215) continue;
            s.ReadExactly(header.AsSpan(0,2)); var size = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0,2));
            if (size < 2 || s.Position + size - 2 > s.Length) throw InvalidImage();
            if (marker is 192 or 193 or 194)
            {
                if (size < 11) throw InvalidImage(); s.ReadExactly(header.AsSpan(0,6));
                height = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1,2)); width = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(3,2));
                if (width == 0 || height == 0 || header[0] != 8 || header[5] is not (1 or 3 or 4) || size != 8 + 3 * header[5]) throw InvalidImage();
                s.Seek(size - 8, SeekOrigin.Current);
            }
            else if (marker == 218) { if (width == 0 || height == 0 || size < 6 || s.Position + size >= s.Length) throw InvalidImage(); return (width,height); }
            else s.Seek(size - 2, SeekOrigin.Current);
        }
        throw InvalidImage();
    }
}
