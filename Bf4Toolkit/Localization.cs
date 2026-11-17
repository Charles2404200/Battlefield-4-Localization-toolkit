using System.Buffers.Binary;
using System.Text;

namespace Bf4Toolkit;

internal sealed record TextEntry(int Index, uint Id, int Occurrence, int Offset,
    int ByteLength, string English);

internal sealed record LocalizationTable(int TableOffset, int TextOffset,
    IReadOnlyList<TextEntry> Entries);

internal static class Localization
{
    public static LocalizationTable Read(byte[] text, byte[] histogram)
    {
        if (text.Length < 32 || histogram.Length < 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(text) != 0x00039000 ||
            BinaryPrimitives.ReadUInt32LittleEndian(histogram) != 0x00039001 ||
            BinaryPrimitives.ReadUInt32LittleEndian(text.AsSpan(4)) + 8L != text.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(histogram.AsSpan(4)) + 8L != histogram.Length)
            throw new InvalidDataException("Unexpected BF4 localization chunk header");

        var tableOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(text.AsSpan(12)) + 8);
        var textOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(text.AsSpan(16)) + 8);
        if (tableOffset < 28 || tableOffset > textOffset || textOffset > text.Length ||
            (textOffset - tableOffset) % 8 != 0)
            throw new InvalidDataException("Invalid localization table boundaries");
        if ((histogram.Length - 12) % 2 != 0)
            throw new InvalidDataException("Invalid localization histogram size");

        var map = new ushort[(histogram.Length - 12) / 2];
        for (var i = 0; i < map.Length; i++)
            map[i] = BinaryPrimitives.ReadUInt16LittleEndian(histogram.AsSpan(12 + i * 2));
        var entries = new List<TextEntry>((textOffset - tableOffset) / 8);
        var occurrences = new Dictionary<uint, int>();
        for (var at = tableOffset; at < textOffset; at += 8)
        {
            var id = BinaryPrimitives.ReadUInt32LittleEndian(text.AsSpan(at));
            var relative = BinaryPrimitives.ReadUInt32LittleEndian(text.AsSpan(at + 4));
            var start = (long)textOffset + relative;
            if (start < textOffset || start >= text.Length)
                throw new InvalidDataException($"String offset outside chunk for ID {id:X8}");
            var end = Array.IndexOf(text, (byte)0, (int)start);
            if (end < 0)
                throw new InvalidDataException($"Unterminated string for ID {id:X8}");
            occurrences.TryGetValue(id, out var occurrence);
            occurrences[id] = occurrence + 1;
            var value = DecodeString(text.AsSpan((int)start, end - (int)start), map, id);
            entries.Add(new TextEntry(entries.Count, id, occurrence, (int)start,
                end - (int)start, value));
        }
        return new LocalizationTable(tableOffset, textOffset, entries);
    }

    private static string DecodeString(ReadOnlySpan<byte> source, ushort[] map, uint id)
    {
        var result = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var b = source[i];
            if (b < 0x80)
            {
                result.Append((char)b);
                continue;
            }
            if (b >= map.Length)
                throw new InvalidDataException($"Histogram missing byte {b:X2} for {id:X8}");
            var code = map[b];
            if (code < 0x80)
            {
                if (++i >= source.Length || source[i] < 0x80)
                    throw new InvalidDataException($"Invalid histogram escape for ID {id:X8}");
                var index = (source[i] - 0x80) + (code << 7);
                if (index >= map.Length)
                    throw new InvalidDataException($"Histogram escape outside map for ID {id:X8}");
                code = map[index];
            }
            result.Append((char)code);
        }
        return result.ToString();
    }
}
