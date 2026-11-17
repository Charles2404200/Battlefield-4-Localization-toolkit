using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bf4Toolkit;

internal static class FontExporter
{
    public static void Run(IEnumerable<GameLayer> layers, string output,
        bool includeWebFonts = true)
    {
        Directory.CreateDirectory(output);
        var allLayers = layers.ToArray();
        var catalog = new CasCatalog();
        foreach (var layer in allLayers)
            catalog.Add(Path.Combine(layer.Root, "cas.cat"));
        var records = new List<object>();
        foreach (var layer in allLayers)
        {
            foreach (var databaseName in includeWebFonts
                         ? new[] { "UI.sb", "Globals.sb" } : new[] { "UI.sb" })
            {
                var databasePath = Path.Combine(layer.Root, "Win32", databaseName);
                if (!File.Exists(databasePath)) continue;
                var database = FrostbiteDb.Read(databasePath);
                foreach (var bundle in database.Child("bundles")?.Children ?? [])
                {
                var bundleName = bundle.Text("path") ?? "";
                var isUiFont = bundleName.Contains("fontcollection_en_",
                    StringComparison.OrdinalIgnoreCase);
                foreach (var kind in new[] { "ebx", "res" })
                foreach (var asset in bundle.Child(kind)?.Children ?? [])
                {
                    var name = asset.Text("name") ??
                        throw new InvalidDataException("Font asset name missing");
                    var isWebFont = name.StartsWith("webbrowser/fonts/", StringComparison.OrdinalIgnoreCase);
                    if (!(isUiFont && name.StartsWith("ui/", StringComparison.OrdinalIgnoreCase)) &&
                        !isWebFont) continue;
                    if (name.Split('/').Any(x => x is ".." or "."))
                        throw new InvalidDataException($"Unsafe asset path: {name}");
                    var sha1 = asset.Bytes("sha1") ??
                        throw new InvalidDataException($"SHA-1 missing for {name}");
                    var expected = asset.Number("originalSize") ??
                        throw new InvalidDataException($"Decoded size missing for {name}");
                    var packedSize = asset.Number("size") ??
                        throw new InvalidDataException($"Packed size missing for {name}");
                    var source = catalog.Find(sha1);
                    if (source.Size != packedSize)
                        throw new InvalidDataException($"Catalog size differs for {name}");
                    var decoded = CasCatalog.ReadPayload(source, packedSize != expected, expected);
                    var gfx = decoded.AsSpan().StartsWith("GFX"u8);
                    if (kind == "res" && !gfx)
                        throw new InvalidDataException($"Font RES is not GFX: {name}");
                    if (gfx && (decoded.Length < 8 ||
                        BinaryPrimitives.ReadUInt32LittleEndian(decoded.AsSpan(4)) != decoded.Length))
                        throw new InvalidDataException($"GFX length header mismatch: {name}");
                    var extension = gfx ? ".gfx" : isWebFont && kind == "res" ? ".ttf" : ".ebx";
                    var relative = Path.Combine(layer.Name,
                        name.Replace('/', Path.DirectorySeparatorChar)) + extension;
                    var destination = Path.GetFullPath(Path.Combine(output, relative));
                    if (!destination.StartsWith(output + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Font output path escaped destination");
                    OutputFiles.WriteImmutable(destination, decoded);
                    records.Add(new
                    {
                        layer = layer.Name, bundle = bundleName, kind, asset = name,
                        source.CatalogPath, source.ArchivePath, source.Offset,
                        packedSize = source.Size, decodedSize = decoded.Length,
                        sha1 = Convert.ToHexString(sha1),
                        sha256 = Convert.ToHexString(SHA256.HashData(decoded)),
                        output = destination
                    });
                    Console.WriteLine($"{layer.Name}: {name}{extension} ({decoded.Length:N0} bytes)");
                    if (isWebFont && kind == "ebx" && TryFindEmbeddedSfnt(decoded, out var sfntOffset))
                    {
                        var font = decoded.AsSpan(sfntOffset).ToArray();
                        var fontPath = Path.ChangeExtension(destination, ".ttf");
                        OutputFiles.WriteImmutable(fontPath, font);
                        records.Add(new
                        {
                            layer = layer.Name, bundle = bundleName, kind = "embedded-ttf", asset = name,
                            source.CatalogPath, source.ArchivePath, source.Offset,
                            packedSize = source.Size, decodedSize = font.Length,
                            sha1 = Convert.ToHexString(sha1),
                            sha256 = Convert.ToHexString(SHA256.HashData(font)),
                            ebxOffset = sfntOffset, output = fontPath
                        });
                        Console.WriteLine($"  embedded TTF +0x{sfntOffset:X}: {fontPath} ({font.Length:N0} bytes)");
                    }
                }
                }
            }
        }
        File.WriteAllText(Path.Combine(output, "font_manifest.json"),
            JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        Console.WriteLine($"Exported {records.Count} font assets");
    }

    private static bool TryFindEmbeddedSfnt(byte[] ebx, out int offset)
    {
        offset = 0;
        for (var at = 0; at + 12 < Math.Min(ebx.Length, 4096); at++)
        {
            var signature = ebx.AsSpan(at, 4);
            if (!signature.SequenceEqual(new byte[] { 0, 1, 0, 0 }) &&
                !signature.SequenceEqual("OTTO"u8)) continue;
            var count = BinaryPrimitives.ReadUInt16BigEndian(ebx.AsSpan(at + 4, 2));
            if (count is < 4 or > 128 || at + 12 + 16 * count > ebx.Length) continue;
            var hasCmap = false;
            var hasHead = false;
            var valid = true;
            for (var i = 0; i < count; i++)
            {
                var record = ebx.AsSpan(at + 12 + i * 16, 16);
                if (record[..4].SequenceEqual("cmap"u8)) hasCmap = true;
                if (record[..4].SequenceEqual("head"u8)) hasHead = true;
                if (record[..4].ToArray().Any(value => value < 0x20 || value > 0x7E))
                    valid = false;
                var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(record[8..12]);
                var length = BinaryPrimitives.ReadUInt32BigEndian(record[12..16]);
                if (dataOffset < 12 + 16 * count || (ulong)at + dataOffset + length > (ulong)ebx.Length)
                    valid = false;
            }
            if (!valid || !hasCmap || !hasHead) continue;
            offset = at;
            return true;
        }
        return false;
    }
}
