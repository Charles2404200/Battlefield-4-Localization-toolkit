using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf4Toolkit;

internal static class TextImporter
{
    public static void Stage(GameLayer patch, string csvPath, string output)
    {
        var toc = FrostbiteDb.ReadSigned(Path.Combine(patch.Root, "Win32", "Loc", "en.toc"));
        var chunks = toc.Child("chunks")?.Children ?? throw new InvalidDataException("No localization chunks");
        if (chunks.Count != 2) throw new InvalidDataException("Expected two localization chunks");
        var sbPath = Path.Combine(patch.Root, "Win32", "Loc", "en.sb");
        var originals = chunks.Select(chunk => CasBlocks.Decode(sbPath,
            checked((long)(chunk.Number("offset") ?? throw new InvalidDataException("Chunk offset missing"))),
            checked((long)(chunk.Number("size") ?? throw new InvalidDataException("Chunk size missing")))).Data).ToArray();
        var table = Localization.Read(originals[0], originals[1]);
        var sheet = ReadSheet(csvPath);
        var ids = table.Entries.Select(entry => entry.Id).ToHashSet();
        if (sheet.Count != ids.Count || sheet.Keys.Except(ids).Any())
            throw new InvalidDataException($"CSV IDs do not match Patch: CSV={sheet.Count}, game={ids.Count}");

        var map = new ushort[(originals[1].Length - 12) / 2];
        for (var i = 0; i < map.Length; i++)
            map[i] = BinaryPrimitives.ReadUInt16LittleEndian(originals[1].AsSpan(12 + i * 2));
        var originalCodes = BuildCodes(map);
        var newHistogram = originals[1].ToArray();
        // Re-encode every row, including untranslated English, so obsolete
        // source-only glyph codes can be removed. BF4's sheet uses fewer than
        // 128 distinct non-ASCII characters, allowing one-byte direct codes.
        var finalCharacters = table.Entries.Select(entry =>
                sheet[entry.Id].Length == 0 ? entry.English : sheet[entry.Id])
            .SelectMany(value => value)
            .Where(ch => ch >= 0x80).Distinct().OrderBy(ch => ch).ToArray();
        var addedCharacterCount = finalCharacters.Count(ch => !originalCodes.ContainsKey(ch));
        if (finalCharacters.Length > 128)
            throw new InvalidDataException($"Compact histogram needs {finalCharacters.Length} direct characters; limit is 128");
        var frequencies = table.Entries.Select(entry =>
                sheet[entry.Id].Length == 0 ? entry.English : sheet[entry.Id])
            .SelectMany(value => value).Where(ch => ch >= 0x80)
            .GroupBy(ch => ch).ToDictionary(group => group.Key, group => group.Count());
        var orderings = new[]
        {
            finalCharacters,
            finalCharacters.Reverse().ToArray(),
            finalCharacters.OrderByDescending(ch => frequencies[ch]).ThenBy(ch => ch).ToArray(),
            finalCharacters.OrderBy(ch => ch & 0xFF).ThenBy(ch => ch).ToArray()
        };
        var bestSize = int.MaxValue;
        var bestStart = 0;
        var bestOrder = 0;
        for (var ordering = 0; ordering < orderings.Length; ordering++)
        for (var start = 0x80; start <= 0x100 - finalCharacters.Length; start++)
        {
            Array.Clear(newHistogram, 12, newHistogram.Length - 12);
            for (var i = 0; i < finalCharacters.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    newHistogram.AsSpan(12 + (start + i) * 2), orderings[ordering][i]);
            var size = CasEncoder.Encode(newHistogram).Length;
            if (size < bestSize) { bestSize = size; bestStart = start; bestOrder = ordering; }
        }
        Array.Clear(map);
        Array.Clear(newHistogram, 12, newHistogram.Length - 12);
        for (var i = 0; i < finalCharacters.Length; i++)
        {
            map[bestStart + i] = orderings[bestOrder][i];
            BinaryPrimitives.WriteUInt16LittleEndian(
                newHistogram.AsSpan(12 + (bestStart + i) * 2), orderings[bestOrder][i]);
        }
        Console.WriteLine($"Compact histogram: {finalCharacters.Length} direct characters, " +
            $"layout at 0x{bestStart:X2} (ordering {bestOrder}, LZ4 estimate {bestSize} bytes)");
        var codes = BuildCodes(map);
        var encoded = new List<(TextEntry Entry, byte[] Bytes, string Expected)>();
        var changed = 0;
        foreach (var entry in table.Entries)
        {
            var translation = sheet[entry.Id];
            var shouldTranslate = translation.Length != 0 && translation != entry.English;
            var bytes = Encode(shouldTranslate ? translation : entry.English, codes, entry.Id);
            if (shouldTranslate) changed++;
            encoded.Add((entry, bytes, shouldTranslate ? translation : entry.English));
        }
        var stringStart = table.TextOffset;
        using var rebuilt = new MemoryStream();
        rebuilt.Write(originals[0].AsSpan(0, stringStart));
        var unique = encoded.Select(row => row.Bytes).Distinct(ByteArrayComparer.Instance)
            .OrderBy(bytes => bytes, ByteArrayComparer.Instance).ToArray();
        var offsets = new Dictionary<byte[], uint>(ByteArrayComparer.Instance);
        foreach (var bytes in unique)
        {
            offsets.Add(bytes, checked((uint)(rebuilt.Length - stringStart)));
            rebuilt.Write(bytes);
            rebuilt.WriteByte(0);
        }
        var required = rebuilt.Length;
        Console.WriteLine($"Text: {changed:N0} translated entries, {addedCharacterCount} new histogram characters; " +
            $"decoded data {required:N0}/{originals[0].Length:N0} bytes");
        if (required > originals[0].Length)
            throw new InvalidDataException("Translated strings exceed the fixed decoded chunk; " +
                "a signed TOC relocation is needed. No game file was changed.");
        var newText = new byte[originals[0].Length];
        rebuilt.ToArray().CopyTo(newText, 0);
        foreach (var row in encoded)
            BinaryPrimitives.WriteUInt32LittleEndian(
                newText.AsSpan(table.TableOffset + row.Entry.Index * 8 + 4), offsets[row.Bytes]);
        var verified = Localization.Read(newText, newHistogram);
        foreach (var row in encoded)
            if (verified.Entries[row.Entry.Index].English != row.Expected)
                throw new InvalidDataException($"Round-trip mismatch at {row.Entry.Id:X8}");

        var textTarget = checked((int)chunks[0].Number("size")!.Value);
        var histTarget = checked((int)chunks[1].Number("size")!.Value);
        var textPacked = CasEncoder.EncodeExact(newText, textTarget);
        var histPacked = CasEncoder.EncodeZlibExact(newHistogram, histTarget);
        Console.WriteLine($"Exact CAS slots: text {textPacked.Length:N0}/{textTarget:N0}, " +
            $"histogram {histPacked.Length:N0}/{histTarget:N0} bytes");
        Directory.CreateDirectory(output);
        OutputFiles.WriteImmutable(Path.Combine(output, "patch_en_text.bin"), newText);
        OutputFiles.WriteImmutable(Path.Combine(output, "patch_en_histogram.bin"), newHistogram);
        OutputFiles.WriteImmutable(Path.Combine(output, "patch_en_text.cas-payload"), textPacked);
        OutputFiles.WriteImmutable(Path.Combine(output, "patch_en_histogram.cas-payload"), histPacked);
        if (!CasBlocks.Decode(Path.Combine(output, "patch_en_text.cas-payload"),
                0, textPacked.Length).Data.AsSpan().SequenceEqual(newText) ||
            !CasBlocks.Decode(Path.Combine(output, "patch_en_histogram.cas-payload"),
                0, histPacked.Length).Data.AsSpan().SequenceEqual(newHistogram))
            throw new InvalidDataException("Staged CAS round-trip failed");
        var metadata = new
        {
            sourceCsv = Path.GetFullPath(csvPath),
            originalTextSha256 = Convert.ToHexString(SHA256.HashData(originals[0])),
            originalHistogramSha256 = Convert.ToHexString(SHA256.HashData(originals[1])),
            textSha256 = Convert.ToHexString(SHA256.HashData(newText)),
            histogramSha256 = Convert.ToHexString(SHA256.HashData(newHistogram)),
            textPackedSha256 = Convert.ToHexString(SHA256.HashData(textPacked)),
            histogramPackedSha256 = Convert.ToHexString(SHA256.HashData(histPacked)),
            changed, newCharacters = addedCharacterCount
        };
        File.WriteAllText(Path.Combine(output, "text_stage.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Staged exact-size CAS chunks in {output}; game files unchanged.");
    }

    private static Dictionary<uint, string> ReadSheet(string path)
    {
        var text = File.ReadAllText(path, new UTF8Encoding(false, true)).TrimStart('\uFEFF');
        var rows = ParseCsv(text, out var repairedQuotes);
        if (repairedQuotes != 0)
            Console.WriteLine($"CSV: closed {repairedQuotes} unclosed quoted cells before the next ID row; source file unchanged.");
        if (rows.Count == 0 || rows[0].Length != 2 ||
            !rows[0][0].Equals("ID", StringComparison.OrdinalIgnoreCase) ||
            !rows[0][1].Equals("Text", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected ID,Text CSV header");
        var sheet = new Dictionary<uint, string>();
        var recovered = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var fields = rows[i];
            if (fields.Length == 1 && fields[0].Length == 0) continue;
            if (fields.Length < 2 || !fields[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                fields[0].Length != 10 || !uint.TryParse(fields[0].AsSpan(2),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                throw new InvalidDataException($"Invalid CSV ID at record {i + 1}");
            if (fields.Length > 2) recovered++;
            var value = string.Join(',', fields.Skip(1)).Normalize(NormalizationForm.FormC);
            if (!sheet.TryAdd(id, value)) throw new InvalidDataException($"Duplicate CSV ID {id:X8}");
        }
        if (recovered != 0) Console.WriteLine($"CSV: joined unquoted commas in {recovered} text cells.");
        return sheet;
    }

    private static List<string[]> ParseCsv(string text, out int repairedQuotes)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        repairedQuotes = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"')
                { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else if (ch is '\r' or '\n' &&
                         StartsIdRow(text, i + (ch == '\r' && i + 1 < text.Length &&
                                                text[i + 1] == '\n' ? 2 : 1)))
                {
                    // Editors sometimes leave an opening ASCII quote without a
                    // matching close. A complete ID at the next physical line
                    // is an unambiguous boundary for this two-column sheet.
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    fields.Add(cell.ToString()); cell.Clear();
                    rows.Add(fields.ToArray()); fields.Clear();
                    quoted = false;
                    repairedQuotes++;
                }
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == ',') { fields.Add(cell.ToString()); cell.Clear(); }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(cell.ToString()); cell.Clear();
                rows.Add(fields.ToArray()); fields.Clear();
            }
            else cell.Append(ch);
        }
        if (quoted) throw new InvalidDataException("Unterminated CSV quote");
        if (cell.Length != 0 || fields.Count != 0) { fields.Add(cell.ToString()); rows.Add(fields.ToArray()); }
        return rows;
    }

    private static bool StartsIdRow(string text, int at)
    {
        if (at + 11 > text.Length || text[at] != '0' ||
            (text[at + 1] is not ('x' or 'X')) || text[at + 10] != ',') return false;
        for (var i = at + 2; i < at + 10; i++)
            if (!Uri.IsHexDigit(text[i])) return false;
        return true;
    }

    private static Dictionary<char, byte[]> BuildCodes(ushort[] map)
    {
        var codes = new Dictionary<char, byte[]>();
        for (var i = 0x80; i < 0x100; i++)
            if (map[i] >= 0x80) codes.TryAdd((char)map[i], [(byte)i]);
        for (var i = 0x80; i < 0x100; i++)
            if (map[i] is > 0 and < 0x80)
                for (var second = 0; second < 128; second++)
                {
                    var at = (map[i] << 7) + second;
                    if (at < map.Length && map[at] >= 0x80)
                        codes.TryAdd((char)map[at], [(byte)i, (byte)(0x80 + second)]);
                }
        return codes;
    }

    private static byte[] Encode(string value, Dictionary<char, byte[]> codes, uint id)
    {
        using var result = new MemoryStream();
        foreach (var ch in value)
        {
            if (ch == 0) throw new InvalidDataException($"NUL in translation {id:X8}");
            if (ch < 0x80) result.WriteByte((byte)ch);
            else if (codes.TryGetValue(ch, out var bytes)) result.Write(bytes);
            else throw new InvalidDataException($"Cannot encode U+{(int)ch:X4} in {id:X8}");
        }
        return result.ToArray();
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? a, byte[]? b) => a is not null && b is not null && a.AsSpan().SequenceEqual(b);
        public int GetHashCode(byte[] bytes)
        {
            var hash = new HashCode();
            foreach (var b in bytes) hash.Add(b);
            return hash.ToHashCode();
        }
        public int Compare(byte[]? a, byte[]? b) =>
            a is null ? b is null ? 0 : -1 : b is null ? 1 : a.AsSpan().SequenceCompareTo(b);
    }
}
