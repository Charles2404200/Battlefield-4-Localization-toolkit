using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf4Toolkit;

internal static class TextExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void ExportSheet(GameLayer patch, string csvPath)
    {
        var loc = Path.Combine(patch.Root, "Win32", "Loc");
        var toc = FrostbiteDb.ReadSigned(Path.Combine(loc, "en.toc"));
        var chunks = toc.Child("chunks")?.Children ??
            throw new InvalidDataException("Patch localization TOC has no chunks");
        if (chunks.Count != 2) throw new InvalidDataException("Expected text and histogram chunks");
        var sb = Path.Combine(loc, "en.sb");
        var decoded = chunks.Select(chunk => CasBlocks.Decode(sb,
            checked((long)(chunk.Number("offset") ?? throw new InvalidDataException("Missing chunk offset"))),
            checked((long)(chunk.Number("size") ?? throw new InvalidDataException("Missing chunk size")))).Data).ToArray();
        var entries = Localization.Read(decoded[0], decoded[1]).Entries;
        var distinct = new Dictionary<uint, string>();
        foreach (var entry in entries)
        {
            if (!distinct.TryAdd(entry.Id, entry.English) &&
                !string.Equals(distinct[entry.Id], entry.English, StringComparison.Ordinal))
                throw new InvalidDataException($"Repeated ID {entry.Id:X8} has different text");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(csvPath))!);
        using var file = new FileStream(csvPath, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(file, new UTF8Encoding(true));
        writer.WriteLine("ID,Text");
        foreach (var (id, value) in distinct.OrderBy(entry => entry.Key))
        {
            writer.Write($"0x{id:X8},");
            WriteCell(writer, value);
            writer.WriteLine();
        }
        Console.WriteLine($"Exported {distinct.Count:N0} IDs to {csvPath}");
    }

    public static void Run(IEnumerable<GameLayer> layers, string output)
    {
        Directory.CreateDirectory(output);
        foreach (var layer in layers)
        {
            var loc = Path.Combine(layer.Root, "Win32", "Loc");
            var tocPath = Path.Combine(loc, "en.toc");
            var sbPath = Path.Combine(loc, "en.sb");
            var toc = FrostbiteDb.ReadSigned(tocPath);
            var chunks = toc.Child("chunks")?.Children ??
                throw new InvalidDataException($"{tocPath} has no chunks");
            if (chunks.Count != 2)
                throw new InvalidDataException($"Expected text and histogram in {tocPath}");
            var loaded = new List<(byte[] Data, object Source)>();
            foreach (var chunk in chunks)
            {
                var offset = checked((long)(chunk.Number("offset") ??
                    throw new InvalidDataException("Chunk offset missing")));
                var size = checked((long)(chunk.Number("size") ??
                    throw new InvalidDataException("Chunk size missing")));
                var (data, blocks) = CasBlocks.Decode(sbPath, offset, size);
                loaded.Add((data, new
                {
                    toc = tocPath, sb = sbPath, offset, packedSize = size,
                    decodedSize = data.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(data)), blocks
                }));
            }
            var table = Localization.Read(loaded[0].Data, loaded[1].Data);
            var raw = Path.Combine(output, "raw");
            OutputFiles.WriteImmutable(Path.Combine(raw, $"{layer.Name}_en_text.bin"), loaded[0].Data);
            OutputFiles.WriteImmutable(Path.Combine(raw, $"{layer.Name}_en_histogram.bin"), loaded[1].Data);
            var csv = Path.Combine(output, $"bf4_en_{layer.Name}.csv");
            if (!File.Exists(csv)) WriteCsv(csv, table.Entries);
            var metadata = new
            {
                layer = layer.Name, count = table.Entries.Count,
                duplicateIds = table.Entries.Count - table.Entries.Select(x => x.Id).Distinct().Count(),
                tableOffset = table.TableOffset, textOffset = table.TextOffset,
                text = loaded[0].Source, histogram = loaded[1].Source
            };
            File.WriteAllText(Path.Combine(output, $"bf4_en_{layer.Name}.meta.json"),
                JsonSerializer.Serialize(metadata, JsonOptions), new UTF8Encoding(false));
            Console.WriteLine($"{layer.Name}: {table.Entries.Count:N0} rows, " +
                $"{metadata.duplicateIds:N0} repeated IDs -> {csv}");
        }
    }

    private static void WriteCsv(string path, IEnumerable<TextEntry> entries)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("entry_index,id_hash,occurrence,english,vietnamese");
        foreach (var entry in entries)
        {
            writer.Write(entry.Index);
            writer.Write(',');
            writer.Write(entry.Id.ToString("X8"));
            writer.Write(',');
            writer.Write(entry.Occurrence);
            writer.Write(',');
            WriteCell(writer, entry.English);
            writer.WriteLine(',');
        }
    }

    private static void WriteCell(TextWriter writer, string value)
    {
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            writer.Write(value);
            return;
        }
        writer.Write('"');
        writer.Write(value.Replace("\"", "\"\""));
        writer.Write('"');
    }
}

internal static class OutputFiles
{
    public static void WriteImmutable(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(data))
                throw new IOException($"Output already edited; refusing to overwrite {path}");
            return;
        }
        File.WriteAllBytes(path, data);
    }
}
