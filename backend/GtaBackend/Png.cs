using System.IO.Compression;

namespace GtaBackend;

/// <summary>Minimaler PNG-Encoder (RGBA8) für Vorschaubilder.</summary>
public static class Png
{
    static readonly uint[] CrcTable = MakeCrcTable();

    static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc(byte[] type, byte[] data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        WriteBE(s, (uint)data.Length);
        s.Write(t);
        s.Write(data);
        WriteBE(s, Crc(t, data));
    }

    static void WriteBE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    /// <param name="bgra">Pixel im BGRA-Format (wie CodeWalker DDSIO.GetPixels)</param>
    public static byte[] EncodeBgra(byte[] bgra, int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        ihdr[0] = (byte)(w >> 24); ihdr[1] = (byte)(w >> 16); ihdr[2] = (byte)(w >> 8); ihdr[3] = (byte)w;
        ihdr[4] = (byte)(h >> 24); ihdr[5] = (byte)(h >> 16); ihdr[6] = (byte)(h >> 8); ihdr[7] = (byte)h;
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(ms, "IHDR", ihdr);

        var raw = new byte[(w * 4 + 1) * h];
        int o = 0;
        for (int y = 0; y < h; y++)
        {
            raw[o++] = 0;
            int row = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                if (i + 3 >= bgra.Length) { o += 4; continue; }
                raw[o++] = bgra[i + 2];
                raw[o++] = bgra[i + 1];
                raw[o++] = bgra[i];
                raw[o++] = bgra[i + 3];
            }
        }
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw);
            Chunk(ms, "IDAT", z.ToArray());
        }
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }
}
