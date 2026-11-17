using System.Buffers.Binary;
using System.IO.Compression;

namespace Bf4Toolkit;

internal static class CasEncoder
{
    public static byte[] EncodeZlibExact(byte[] source, int targetSize)
    {
        var compressedSizes = new List<int>();
        var levels = new[] { CompressionLevel.SmallestSize, CompressionLevel.Optimal,
            CompressionLevel.Fastest };
        foreach (var level in levels)
        {
            using var buffer = new MemoryStream();
            using (var zipper = new ZLibStream(buffer, level, leaveOpen: true)) zipper.Write(source);
            var zlib = buffer.ToArray();
            compressedSizes.Add(zlib.Length);
            var deficit = targetSize - 8 - zlib.Length;
            if (zlib.Length < 8 || deficit < 5 || deficit % 5 != 0 ||
                (zlib[2] & 1) == 0) continue;
            var emptyBlocks = deficit / 5;
            var deflateLength = zlib.Length - 6;
            for (var finalBits = 1; finalBits <= 8; finalBits++)
            {
                var writer = new DeflateBits();
                for (var i = 0; i < deflateLength - 1; i++)
                    writer.WriteByte((byte)(i == 0 ? zlib[2] & 0xFE : zlib[2 + i]));
                var last = zlib[2 + deflateLength - 1];
                for (var bit = 0; bit < finalBits; bit++) writer.WriteBit((last >> bit) & 1);
                for (var i = 0; i < emptyBlocks; i++)
                {
                    writer.WriteBit(i == emptyBlocks - 1 ? 1 : 0);
                    writer.WriteBit(0); writer.WriteBit(0); // stored block
                    writer.Align();
                    foreach (var b in new byte[] { 0, 0, 255, 255 }) writer.WriteByte(b);
                }
                var deflate = writer.ToArray();
                var replacement = new byte[2 + deflate.Length + 4];
                zlib.AsSpan(0, 2).CopyTo(replacement);
                deflate.CopyTo(replacement, 2);
                zlib.AsSpan(zlib.Length - 4).CopyTo(replacement.AsSpan(replacement.Length - 4));
                if (replacement.Length + 8 != targetSize) continue;
                try
                {
                    using var input = new MemoryStream(replacement);
                    using var decoder = new ZLibStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    decoder.CopyTo(output);
                    if (!output.ToArray().AsSpan().SequenceEqual(source)) continue;
                    var cas = MakeBlock(replacement, source.Length, 2);
                    if (cas.Length == targetSize) return cas;
                }
                catch (InvalidDataException) { }
            }
        }
        // A sync flush emits a valid, nonfinal DEFLATE block. Let zlib create
        // the padding itself instead of guessing the bit position at which the
        // final Huffman block ends. Its final empty block remains well formed.
        foreach (var level in levels)
        {
            for (var count = 1; count <= 8; count++)
                if (TryFlushed(source, targetSize, level, source.Length, count) is { } exact)
                    return exact;
            for (var split = 16; split < Math.Min(source.Length, 1024); split += 8)
                for (var count = 1; count <= 8; count++)
                    if (TryFlushed(source, targetSize, level, split, count) is { } exact)
                        return exact;
        }
        throw new InvalidDataException($"Cannot encode valid zlib stream in exactly {targetSize} CAS bytes " +
            $"(payload capacity {targetSize - 8}, zlib sizes {string.Join('/', compressedSizes)})");
    }

    private static byte[]? TryFlushed(byte[] source, int targetSize,
        CompressionLevel level, int split, int flushCount)
    {
        using var buffer = new MemoryStream();
        using (var zipper = new ZLibStream(buffer, level, leaveOpen: true))
        {
            zipper.Write(source.AsSpan(0, split));
            for (var i = 0; i < flushCount; i++) zipper.Flush();
            zipper.Write(source.AsSpan(split));
        }
        var packed = buffer.ToArray();
        if (packed.Length + 8 != targetSize) return null;
        using var input = new MemoryStream(packed);
        using var decoder = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray().AsSpan().SequenceEqual(source)
            ? MakeBlock(packed, source.Length, 2) : null;
    }

    private sealed class DeflateBits
    {
        private readonly List<byte> _bytes = [];
        private int _bit;
        public void WriteBit(int value)
        {
            if (_bit == 0) _bytes.Add(0);
            if (value != 0) _bytes[^1] |= (byte)(1 << _bit);
            _bit = (_bit + 1) & 7;
        }
        public void Align() { while (_bit != 0) WriteBit(0); }
        public void WriteByte(byte value)
        {
            if (_bit != 0) throw new InvalidOperationException("Deflate writer is not aligned");
            _bytes.Add(value);
        }
        public byte[] ToArray() => _bytes.ToArray();
    }
    public static byte[] EncodeExactPaddedFont(byte[] font, int decodedSize, int packedSize)
    {
        if (font.Length < 12 || font.Length > decodedSize || font.Length >= 65536)
            throw new InvalidDataException("Subset font does not fit the first decoded CAS block");
        var padded = new byte[decodedSize];
        font.CopyTo(padded, 0);
        if (decodedSize > 131072)
            throw new InvalidDataException("Padded font encoder supports at most two CAS blocks");
        var firstSize = Math.Min(65536, decodedSize);
        var compressedFont = Lz4Compress(font);
        var (finalStart, finalLiteralCount) = FindFinalLz4Sequence(compressedFont);
        var compressedPrefix = compressedFont.AsSpan(0, finalStart).ToArray();
        var sequenceDecodedSize = firstSize - font.Length + finalLiteralCount;
        var firstOptions = new Dictionary<int, (int FinalLiterals, int LiteralZeros)>();
        for (var finalLiterals = 5; finalLiterals <= 15; finalLiterals++)
        {
            var maxLiteralZeros = firstSize - font.Length - finalLiterals - 8;
            for (var literalZeros = 1; literalZeros <= maxLiteralZeros; literalZeros++)
            {
                var literal = finalLiteralCount + literalZeros;
                var length = compressedPrefix.Length +
                    ZeroBlockSize(sequenceDecodedSize, literal, finalLiterals);
                firstOptions.TryAdd(length, (finalLiterals, literalZeros));
            }
        }
        byte[]? second = null;
        var firstTarget = packedSize;
        if (decodedSize > 65536)
        {
            var secondSize = decodedSize - 65536;
            var minimum = EncodeBlock(padded.AsSpan(65536, secondSize));
            if (firstOptions.ContainsKey(packedSize - minimum.Length))
            {
                second = minimum;
                firstTarget = packedSize - second.Length;
            }
            else
            {
                for (var final = 5; final <= 15 && second is null; final++)
                {
                    for (var prefixZeros = 1; prefixZeros <= secondSize - final - 8; prefixZeros++)
                    {
                        var secondLength = ZeroBlockSize(secondSize, prefixZeros, final);
                        if (!firstOptions.ContainsKey(packedSize - secondLength)) continue;
                        second = MakeZeroBlock(secondSize, prefixZeros, final);
                        firstTarget = packedSize - second.Length;
                        break;
                    }
                }
                if (second is null)
                {
                    var raw = MakeBlock(new byte[secondSize], secondSize, 0);
                    if (firstOptions.ContainsKey(packedSize - raw.Length))
                    {
                        second = raw;
                        firstTarget = packedSize - second.Length;
                    }
                }
            }
        }
        if (!firstOptions.TryGetValue(firstTarget, out var choice))
            throw new InvalidDataException($"Cannot fit subset font in exact CAS slot: target={packedSize:N0}, font={font.Length:N0}, compressed={compressedFont.Length:N0}");
        var firstLiteral = finalLiteralCount + choice.LiteralZeros;
        var firstMatch = firstSize - font.Length - choice.LiteralZeros - choice.FinalLiterals;
        using var encoded = new MemoryStream(firstTarget - 8);
        encoded.Write(compressedPrefix);
        encoded.WriteByte((byte)(Math.Min(firstLiteral, 15) << 4 |
            Math.Min(firstMatch - 4, 15)));
        WriteLength(encoded, firstLiteral - 15);
        encoded.Write(font.AsSpan(font.Length - finalLiteralCount));
        encoded.Write(new byte[choice.LiteralZeros]);
        encoded.WriteByte(1);
        encoded.WriteByte(0);
        WriteLength(encoded, firstMatch - 4 - 15);
        encoded.WriteByte((byte)(choice.FinalLiterals << 4));
        encoded.Write(new byte[choice.FinalLiterals]);
        var first = MakeBlock(encoded.ToArray(), firstSize, 9);
        using var result = new MemoryStream(packedSize);
        result.Write(first);
        if (second is not null) result.Write(second);
        var payload = result.ToArray();
        if (payload.Length != packedSize || !Decode(payload).AsSpan().SequenceEqual(padded))
            throw new InvalidDataException("Padded font CAS round-trip mismatch");
        return payload;
    }

    private static (int Start, int LiteralCount) FindFinalLz4Sequence(byte[] compressed)
    {
        var at = 0;
        while (at < compressed.Length)
        {
            var start = at;
            var token = compressed[at++];
            var literal = token >> 4;
            if (literal == 15) literal += ReadExtra(compressed, ref at);
            if (literal > compressed.Length - at)
                throw new InvalidDataException("Invalid compressed font literal length");
            at += literal;
            if (at == compressed.Length) return (start, literal);
            if (at + 2 > compressed.Length)
                throw new InvalidDataException("Invalid compressed font match offset");
            at += 2;
            if ((token & 15) == 15) ReadExtra(compressed, ref at);
        }
        throw new InvalidDataException("No final LZ4 font sequence");
    }

    public static byte[] Encode(byte[] source)
    {
        using var result = new MemoryStream();
        Span<byte> header = stackalloc byte[8];
        for (var at = 0; at < source.Length; at += 65536)
        {
            var size = Math.Min(65536, source.Length - at);
            var block = source.AsSpan(at, size);
            var compressed = Lz4Compress(block);
            var raw = compressed.Length >= size;
            var payload = raw ? block.ToArray() : compressed;
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)size);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..],
                (ushort)((raw ? 0x7000 : 0x7009) | ((payload.Length >> 16) << 8)));
            BinaryPrimitives.WriteUInt16BigEndian(header[6..], (ushort)(payload.Length & 0xFFFF));
            result.Write(header);
            result.Write(payload);
        }
        return result.ToArray();
    }

    public static byte[] EncodeExact(byte[] source, int targetSize)
    {
        var blocks = new List<byte[]>();
        var zeroBlocks = new List<int>();
        var total = 0;
        for (var at = 0; at < source.Length; at += 65536)
        {
            var size = Math.Min(65536, source.Length - at);
            var block = source.AsSpan(at, size);
            var packed = EncodeBlock(block);
            if (block.IndexOfAnyExcept((byte)0) < 0) zeroBlocks.Add(blocks.Count);
            blocks.Add(packed);
            total += packed.Length;
        }
        if (total > targetSize)
            throw new InvalidDataException($"CAS encoder needs {total:N0} bytes; slot has {targetSize:N0}");
        var missing = targetSize - total;
        var zeroCapacity = zeroBlocks.Sum(index =>
        {
            var size = Math.Min(65536, source.Length - index * 65536);
            return MakeBlock(new byte[size], size, 0).Length - blocks[index].Length;
        });
        if (missing > zeroCapacity)
        {
            var zeroSet = zeroBlocks.ToHashSet();
            foreach (var index in Enumerable.Range(0, blocks.Count)
                .Where(index => !zeroSet.Contains(index))
                .OrderBy(index => MakeBlock(
                    source.AsSpan(index * 65536,
                        Math.Min(65536, source.Length - index * 65536)).ToArray(),
                    Math.Min(65536, source.Length - index * 65536), 0).Length
                    - blocks[index].Length))
            {
                if (missing <= zeroCapacity) break;
                var size = Math.Min(65536, source.Length - index * 65536);
                var raw = MakeBlock(source.AsSpan(index * 65536, size).ToArray(), size, 0);
                var gain = raw.Length - blocks[index].Length;
                if (gain <= 0 || gain > missing) continue;
                blocks[index] = raw;
                missing -= gain;
            }
        }
        foreach (var index in zeroBlocks)
        {
            if (missing == 0) break;
            var size = Math.Min(65536, source.Length - index * 65536);
            var original = blocks[index].Length;
            var raw = MakeBlock(new byte[size], size, 0);
            var rawGain = raw.Length - original;
            if (missing >= rawGain)
            {
                blocks[index] = raw;
                missing -= rawGain;
                continue;
            }
            byte[]? best = null;
            var bestGain = 0;
            for (var finalLiterals = 5; finalLiterals <= 15; finalLiterals++)
            {
                var low = 1;
                var high = size - finalLiterals - 4;
                while (low <= high)
                {
                    var literalPrefix = low + (high - low) / 2;
                    var packedSize = ZeroBlockSize(size, literalPrefix, finalLiterals);
                    var gain = packedSize - original;
                    if (gain <= missing && gain > bestGain)
                    {
                        bestGain = gain;
                        best = MakeZeroBlock(size, literalPrefix, finalLiterals);
                    }
                    if (gain == missing) break;
                    if (gain < missing) low = literalPrefix + 1;
                    else high = literalPrefix - 1;
                }
                if (bestGain == missing) break;
            }
            if (best is not null)
            {
                blocks[index] = best;
                missing -= bestGain;
            }
        }
        if (missing != 0)
        {
            // A localization chunk often ends with a partly filled block: text
            // followed by zero padding. Grow its LZ4 literal run to fill the
            // signed TOC slot while preserving the decoded block boundaries.
            for (var index = 0; index < blocks.Count && missing != 0; index++)
            {
                var start = index * 65536;
                var size = Math.Min(65536, source.Length - start);
                var block = source.AsSpan(start, size);
                var lastNonzero = size - 1;
                while (lastNonzero >= 0 && block[lastNonzero] == 0) lastNonzero--;
                if (lastNonzero < 12 || lastNonzero >= size - 20) continue;
                try
                {
                    var padded = EncodeExactPaddedFont(block[..(lastNonzero + 1)].ToArray(),
                        size, checked(blocks[index].Length + missing));
                    blocks[index] = padded;
                    missing = 0;
                }
                catch (InvalidDataException) { /* This block has insufficient zero padding. */ }
            }
        }
        if (missing != 0)
            throw new InvalidDataException($"CAS exact-slot encoding is short by {missing} bytes");
        using var result = new MemoryStream(targetSize);
        foreach (var block in blocks) result.Write(block);
        if (result.Length != targetSize) throw new InvalidDataException("CAS packing length mismatch");
        return result.ToArray();
    }

    private static byte[] EncodeBlock(ReadOnlySpan<byte> source)
    {
        var compressed = Lz4Compress(source);
        return compressed.Length >= source.Length ?
            MakeBlock(source.ToArray(), source.Length, 0) :
            MakeBlock(compressed, source.Length, 9);
    }

    private static byte[] MakeBlock(byte[] payload, int decodedSize, int kind)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)decodedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4),
            (ushort)(0x7000 | kind | ((payload.Length >> 16) << 8)));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(6), (ushort)payload.Length);
        payload.CopyTo(result, 8);
        return result;
    }

    private static int ZeroBlockSize(int decodedSize, int literalPrefix, int finalLiterals)
    {
        var match = decodedSize - literalPrefix - finalLiterals;
        if (literalPrefix < 1 || match < 4 || finalLiterals < 5 || finalLiterals > 15)
            return int.MaxValue;
        return 8 + 1 + ExtraLengthBytes(literalPrefix - 15) + literalPrefix + 2 +
            ExtraLengthBytes(match - 4 - 15) + 1 + finalLiterals;
    }

    private static int ExtraLengthBytes(int extra) => extra < 0 ? 0 : extra / 255 + 1;

    private static byte[] MakeZeroBlock(int size, int literalPrefix, int finalLiterals)
    {
        var match = size - literalPrefix - finalLiterals;
        using var compressed = new MemoryStream(ZeroBlockSize(size, literalPrefix, finalLiterals) - 8);
        compressed.WriteByte((byte)(Math.Min(literalPrefix, 15) << 4 |
            Math.Min(match - 4, 15)));
        WriteLength(compressed, literalPrefix - 15);
        compressed.Write(new byte[literalPrefix]);
        compressed.WriteByte(1);
        compressed.WriteByte(0);
        WriteLength(compressed, match - 4 - 15);
        compressed.WriteByte((byte)(finalLiterals << 4));
        compressed.Write(new byte[finalLiterals]);
        return MakeBlock(compressed.ToArray(), size, 9);
    }

    public static byte[] Decode(byte[] source)
    {
        using var result = new MemoryStream();
        var at = 0;
        while (at < source.Length)
        {
            if (source.Length - at < 8) throw new InvalidDataException("Short CAS header");
            var size = checked((int)BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(at)));
            var type = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(at + 4));
            var packed = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(at + 6)) +
                ((type >> 8 & 0xF) << 16);
            at += 8;
            if (size < 1 || size > 65536 || packed < 1 || packed > source.Length - at)
                throw new InvalidDataException("Invalid CAS block size");
            byte[] decoded = (type & 0x7F) switch
            {
                0 => source.AsSpan(at, packed).ToArray(),
                9 => Lz4Decompress(source.AsSpan(at, packed), size),
                _ => throw new InvalidDataException($"Unsupported CAS type 0x{type:X4}")
            };
            if (decoded.Length != size) throw new InvalidDataException("CAS decoded size mismatch");
            result.Write(decoded);
            at += packed;
        }
        return result.ToArray();
    }

    private static byte[] Lz4Compress(ReadOnlySpan<byte> source)
    {
        using var result = new MemoryStream(source.Length);
        var heads = new int[1 << 16];
        var chains = new int[source.Length];
        Array.Fill(heads, -1);
        var anchor = 0;
        var at = 0;
        while (at <= source.Length - 12)
        {
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(source[at..]);
            var hash = (int)((sequence * 2654435761U) >> 16);
            var previous = heads[hash];
            chains[at] = previous;
            heads[hash] = at;
            var bestLength = 0;
            var bestPosition = -1;
            for (var attempts = 0; previous >= 0 && at - previous <= 65535 &&
                    attempts < 96; attempts++, previous = chains[previous])
            {
                if (source[previous] != source[at] ||
                    !source.Slice(previous, 4).SequenceEqual(source.Slice(at, 4))) continue;
                var length = 4;
                while (at + length < source.Length - 5 &&
                    source[previous + length] == source[at + length]) length++;
                if (length > bestLength) { bestLength = length; bestPosition = previous; }
                if (length >= 1024) break;
            }
            if (bestLength < 4) { at++; continue; }
            var literal = at - anchor;
            var token = (byte)(Math.Min(literal, 15) << 4 | Math.Min(bestLength - 4, 15));
            result.WriteByte(token);
            WriteLength(result, literal - 15);
            result.Write(source.Slice(anchor, literal));
            var distance = at - bestPosition;
            result.WriteByte((byte)distance);
            result.WriteByte((byte)(distance >> 8));
            WriteLength(result, bestLength - 4 - 15);
            var end = at + bestLength;
            for (var p = at + 1; p < end && p <= source.Length - 4; p++)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(source[p..]);
                var h = (int)((value * 2654435761U) >> 16);
                chains[p] = heads[h];
                heads[h] = p;
            }
            at = end;
            anchor = at;
        }
        var tail = source.Length - anchor;
        result.WriteByte((byte)(Math.Min(tail, 15) << 4));
        WriteLength(result, tail - 15);
        result.Write(source[anchor..]);
        return result.ToArray();
    }

    private static void WriteLength(Stream output, int extra)
    {
        if (extra < 0) return;
        while (extra >= 255) { output.WriteByte(255); extra -= 255; }
        output.WriteByte((byte)extra);
    }

    private static byte[] Lz4Decompress(ReadOnlySpan<byte> source, int size)
    {
        var output = new byte[size];
        var input = 0;
        var at = 0;
        while (input < source.Length)
        {
            var token = source[input++];
            var literal = token >> 4;
            if (literal == 15) literal += ReadExtra(source, ref input);
            if (input + literal > source.Length || at + literal > output.Length)
                throw new InvalidDataException("LZ4 literal overflow");
            source.Slice(input, literal).CopyTo(output.AsSpan(at));
            input += literal;
            at += literal;
            if (input == source.Length) break;
            if (input + 2 > source.Length) throw new InvalidDataException("Short LZ4 offset");
            var distance = BinaryPrimitives.ReadUInt16LittleEndian(source[input..]);
            input += 2;
            if (distance == 0 || distance > at) throw new InvalidDataException("Invalid LZ4 distance");
            var match = (token & 15) + 4;
            if ((token & 15) == 15) match += ReadExtra(source, ref input);
            if (at + match > output.Length) throw new InvalidDataException("LZ4 match overflow");
            for (var i = 0; i < match; i++) output[at + i] = output[at + i - distance];
            at += match;
        }
        if (at != size) throw new InvalidDataException($"LZ4 decoded {at}, expected {size}");
        return output;
    }

    private static int ReadExtra(ReadOnlySpan<byte> source, ref int at)
    {
        var total = 0;
        byte value;
        do
        {
            if (at >= source.Length) throw new InvalidDataException("Short LZ4 length");
            value = source[at++];
            total = checked(total + value);
        } while (value == 255);
        return total;
    }
}
