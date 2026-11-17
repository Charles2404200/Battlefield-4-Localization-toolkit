using System.Buffers.Binary;
using System.IO.Compression;

namespace Bf4Toolkit;

internal sealed record BlockInfo(long FileOffset, int DecodedOffset, int PackedSize,
    int DecodedSize, int Type);

internal static class CasBlocks
{
    public static (byte[] Data, List<BlockInfo> Blocks) Decode(string file, long offset, long length)
    {
        using var stream = File.OpenRead(file);
        if (offset < 0 || length < 0 || offset + length > stream.Length)
            throw new InvalidDataException($"CAS extent outside file: {file}");
        stream.Position = offset;
        var end = offset + length;
        using var decoded = new MemoryStream();
        var blocks = new List<BlockInfo>();
        Span<byte> header = stackalloc byte[8];
        while (stream.Position < end)
        {
            var blockOffset = stream.Position;
            if (end - stream.Position < header.Length)
                throw new InvalidDataException($"Truncated block header at 0x{blockOffset:X}");
            stream.ReadExactly(header);
            var outputSize = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            var packedSize = BinaryPrimitives.ReadUInt16BigEndian(header[6..])
                + ((type >> 8 & 0xF) << 16);
            if (outputSize is < 1 or > 65536 || packedSize < 1 ||
                stream.Position + packedSize > end)
                throw new InvalidDataException($"Invalid CAS block at 0x{blockOffset:X}");
            var packed = new byte[packedSize];
            stream.ReadExactly(packed);
            var block = (type & 0x7F) switch
            {
                0 => packed,
                2 => DecodeZlib(packed),
                9 => DecodeLz4(packed, outputSize),
                _ => throw new InvalidDataException($"Unsupported CAS codec 0x{type:X4} at 0x{blockOffset:X}")
            };
            if (block.Length != outputSize)
                throw new InvalidDataException($"Wrong decoded size at 0x{blockOffset:X}");
            blocks.Add(new BlockInfo(blockOffset, checked((int)decoded.Length),
                packedSize, outputSize, type));
            decoded.Write(block);
        }
        if (stream.Position != end)
            throw new InvalidDataException("CAS entry did not end at the TOC/CAT boundary");
        return (decoded.ToArray(), blocks);
    }

    private static byte[] DecodeZlib(byte[] source)
    {
        using var input = new MemoryStream(source, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] DecodeLz4(ReadOnlySpan<byte> source, int expected)
    {
        var output = new byte[expected];
        var inputAt = 0;
        var outputAt = 0;
        while (inputAt < source.Length)
        {
            var token = source[inputAt++];
            var literal = token >> 4;
            if (literal == 15) literal += ReadExtra(source, ref inputAt);
            if (inputAt + literal > source.Length || outputAt + literal > expected)
                throw new InvalidDataException("LZ4 literal extends outside block");
            source.Slice(inputAt, literal).CopyTo(output.AsSpan(outputAt));
            inputAt += literal;
            outputAt += literal;
            if (inputAt == source.Length) break;
            if (inputAt + 2 > source.Length)
                throw new InvalidDataException("LZ4 distance missing");
            var distance = BinaryPrimitives.ReadUInt16LittleEndian(source[inputAt..]);
            inputAt += 2;
            if (distance < 1 || distance > outputAt)
                throw new InvalidDataException("Invalid LZ4 distance");
            var match = (token & 15) + 4;
            if ((token & 15) == 15) match += ReadExtra(source, ref inputAt);
            if (outputAt + match > expected)
                throw new InvalidDataException("LZ4 match extends outside block");
            for (var i = 0; i < match; i++)
                output[outputAt + i] = output[outputAt + i - distance];
            outputAt += match;
        }
        if (outputAt != expected)
            throw new InvalidDataException($"LZ4 decoded {outputAt} bytes, expected {expected}");
        return output;
    }

    private static int ReadExtra(ReadOnlySpan<byte> source, ref int position)
    {
        var total = 0;
        byte value;
        do
        {
            if (position >= source.Length)
                throw new InvalidDataException("LZ4 length truncated");
            value = source[position++];
            total = checked(total + value);
        } while (value == 255);
        return total;
    }
}
