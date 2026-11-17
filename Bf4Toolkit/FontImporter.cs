using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bf4Toolkit;

internal sealed record FontStage(string Asset, string OldSha1, string OldDecodedSha256,
    int OldDecodedSize, int OldPackedSize, string NewSha1, string NewDecodedSha256,
    int NewDecodedSize, int NewPackedSize, string PayloadFile);
internal sealed record FontBackupFile(string Path, string Name, string OriginalSha256,
    string InstalledSha256);
internal sealed record FontBackup(string CasPath, long OriginalCasLength,
    string AppendedSha256, List<FontBackupFile> Files);

internal static class FontImporter
{
    private static readonly string[] Names =
    [
        "ui/static/fontcollection_en_fontlibwin32",
        "ui/static/ondemandfontcollection/eulafontcollection_en_fontlibwin32",
        "ui/static/ondemandfontcollection/storefontcollection_en_fontlibwin32"
    ];

    public static void Stage(GameLayer data, GameLayer patch, string inputDirectory,
        string outputDirectory)
    {
        var catalog = new CasCatalog();
        catalog.Add(Path.Combine(data.Root, "cas.cat"));
        catalog.Add(Path.Combine(patch.Root, "cas.cat"));
        var ui = FrostbiteDb.Read(Path.Combine(data.Root, "Win32", "UI.sb"));
        var stages = new List<FontStage>();
        Directory.CreateDirectory(outputDirectory);
        foreach (var name in Names)
        {
            var asset = FindResource(ui, name);
            var oldSha = asset.Bytes("sha1") ?? throw new InvalidDataException("Font SHA-1 missing");
            var oldDecodedSize = checked((int)asset.Number("originalSize")!.Value);
            var oldPackedSize = checked((int)asset.Number("size")!.Value);
            var source = catalog.Find(oldSha);
            if (!source.CatalogPath.Equals(Path.Combine(data.Root, "cas.cat"),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unexpected font catalog layer: {name}");
            var original = CasCatalog.ReadPayload(source, oldPackedSize != oldDecodedSize,
                (ulong)oldDecodedSize);
            var path = Path.Combine(inputDirectory, "data", name.Replace('/', Path.DirectorySeparatorChar) + ".gfx");
            var replacement = File.ReadAllBytes(path);
            ValidateGfx(replacement, name);
            if (replacement.AsSpan().SequenceEqual(original))
            { Console.WriteLine($"Unchanged: {name}"); continue; }
            var packed = CasEncoder.Encode(replacement);
            var payloadName = name.Split('/')[^1] + ".cas-payload";
            var payloadPath = Path.Combine(outputDirectory, payloadName);
            OutputFiles.WriteImmutable(payloadPath, packed);
            if (!CasBlocks.Decode(payloadPath, 0, packed.Length).Data.AsSpan().SequenceEqual(replacement))
                throw new InvalidDataException($"Font CAS round-trip failed: {name}");
            stages.Add(new FontStage(name, Convert.ToHexString(oldSha), Sha256(original),
                oldDecodedSize, oldPackedSize, Convert.ToHexString(SHA1.HashData(packed)),
                Sha256(replacement), replacement.Length, packed.Length, payloadName));
            Console.WriteLine($"{name}: GFX {oldDecodedSize:N0}->{replacement.Length:N0}, " +
                $"CAS {oldPackedSize:N0}->{packed.Length:N0}");
        }
        File.WriteAllText(Path.Combine(outputDirectory, "font_stage.json"),
            JsonSerializer.Serialize(stages, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(stages.Count == 0
            ? "All three GFX fonts already match the game; nothing to install."
            : $"Staged {stages.Count} enlarged fonts; game files unchanged.");
    }

    public static void Install(GameLayer data, GameLayer patch, string stageDirectory,
        string backupDirectory)
    {
        TextInstaller.EnsureStopped();
        if (Directory.Exists(backupDirectory) || File.Exists(backupDirectory))
            throw new IOException($"Backup path already exists: {backupDirectory}");
        var stages = JsonSerializer.Deserialize<List<FontStage>>(
            File.ReadAllText(Path.Combine(stageDirectory, "font_stage.json")))
            ?? throw new InvalidDataException("Invalid font stage manifest");
        if (stages.Count == 0 || stages.Count > Names.Length ||
            stages.Select(s => s.Asset).Distinct().Count() != stages.Count ||
            stages.Any(s => !Names.Contains(s.Asset)))
            throw new InvalidDataException("Unexpected staged font asset list");
        var dataUiPath = Path.Combine(data.Root, "Win32", "UI.sb");
        var patchUiPath = Path.Combine(patch.Root, "Win32", "UI.sb");
        var catalogPath = Path.Combine(data.Root, "cas.cat");
        var files = new[] { dataUiPath, patchUiPath, catalogPath };
        var originals = files.ToDictionary(path => path, File.ReadAllBytes,
            StringComparer.OrdinalIgnoreCase);
        var changed = files.ToDictionary(path => path, path => originals[path].ToArray(),
            StringComparer.OrdinalIgnoreCase);
        var catalog = new CasCatalog();
        catalog.Add(catalogPath);
        catalog.Add(Path.Combine(patch.Root, "cas.cat"));
        var archives = Directory.GetFiles(data.Root, "cas_*.cas")
            .Select(path => new FileInfo(path)).OrderBy(file => file.Length).ToArray();
        if (archives.Length == 0) throw new InvalidDataException("No Data CAS archives");
        var target = archives[0];
        var archiveNumberText = Path.GetFileNameWithoutExtension(target.Name)[4..];
        if (!uint.TryParse(archiveNumberText, out var archiveNumber))
            throw new InvalidDataException("Invalid CAS archive number");
        var originalCasLength = target.Length;
        using var append = new MemoryStream();
        foreach (var stage in stages)
        {
            var oldSha = Convert.FromHexString(stage.OldSha1);
            var source = catalog.Find(oldSha);
            if (!source.CatalogPath.Equals(catalogPath, StringComparison.OrdinalIgnoreCase) ||
                source.Size != stage.OldPackedSize)
                throw new InvalidDataException($"Original font location changed: {stage.Asset}");
            var oldFont = CasCatalog.ReadPayload(source,
                stage.OldPackedSize != stage.OldDecodedSize, (ulong)stage.OldDecodedSize);
            if (!Sha256(oldFont).Equals(stage.OldDecodedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Original font changed: {stage.Asset}");
            var payloadPath = Path.Combine(stageDirectory, stage.PayloadFile);
            var payload = File.ReadAllBytes(payloadPath);
            if (payload.Length != stage.NewPackedSize ||
                !Convert.ToHexString(SHA1.HashData(payload)).Equals(stage.NewSha1,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Staged font payload changed: {stage.Asset}");
            var font = CasBlocks.Decode(payloadPath, 0, payload.Length).Data;
            ValidateGfx(font, stage.Asset);
            if (font.Length != stage.NewDecodedSize ||
                !Sha256(font).Equals(stage.NewDecodedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Staged font decode mismatch: {stage.Asset}");
            var payloadOffset = checked(originalCasLength + append.Length + 32);
            if (payloadOffset + payload.Length > uint.MaxValue)
                throw new InvalidDataException("CAS append exceeds 32-bit catalog offset");
            var record = new byte[32];
            new byte[] { 0xFA, 0xCE, 0x0F, 0xF0 }.AsSpan().CopyTo(record);
            var newSha = Convert.FromHexString(stage.NewSha1);
            newSha.CopyTo(record.AsSpan(4, 20));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24, 4), (uint)payload.Length);
            append.Write(record);
            append.Write(payload);
            PatchCatalog(changed[catalogPath], source, oldSha, newSha,
                checked((uint)payloadOffset), checked((uint)payload.Length), archiveNumber);
            PatchUi(changed[dataUiPath], stage, newSha, required: true);
            PatchUi(changed[patchUiPath], stage, newSha,
                required: stage.Asset == Names[0]);
        }
        var appended = append.ToArray();
        var backupFiles = new List<FontBackupFile>();
        Directory.CreateDirectory(backupDirectory);
        for (var i = 0; i < files.Length; i++)
        {
            var name = $"file_{i}.original";
            File.WriteAllBytes(Path.Combine(backupDirectory, name), originals[files[i]]);
            backupFiles.Add(new FontBackupFile(files[i], name, Sha256(originals[files[i]]),
                Sha256(changed[files[i]])));
        }
        var backup = new FontBackup(target.FullName, originalCasLength,
            Sha256(appended), backupFiles);
        File.WriteAllText(Path.Combine(backupDirectory, "font_backup.json"),
            JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            using (var cas = new FileStream(target.FullName, FileMode.Open,
                       FileAccess.ReadWrite, FileShare.None))
            {
                if (cas.Length != originalCasLength)
                    throw new IOException("CAS archive changed before append");
                cas.Position = originalCasLength;
                cas.Write(appended);
                cas.Flush(flushToDisk: true);
            }
            foreach (var file in files) File.WriteAllBytes(file, changed[file]);
            foreach (var file in files)
                if (!Sha256(File.ReadAllBytes(file)).Equals(Sha256(changed[file]),
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Installed metadata differs: {file}");
            using var verifyCas = File.OpenRead(target.FullName);
            verifyCas.Position = originalCasLength;
            var readback = new byte[appended.Length];
            verifyCas.ReadExactly(readback);
            if (!readback.AsSpan().SequenceEqual(appended))
                throw new IOException("CAS appended bytes differ after write");
            var installedCatalog = new CasCatalog();
            installedCatalog.Add(catalogPath);
            foreach (var stage in stages)
            {
                var entry = installedCatalog.Find(Convert.FromHexString(stage.NewSha1));
                var decoded = CasCatalog.ReadPayload(entry, true, (ulong)stage.NewDecodedSize);
                if (!Sha256(decoded).Equals(stage.NewDecodedSha256,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Installed font CAS decode mismatch: {stage.Asset}");
                VerifyUi(changed[dataUiPath], stage);
                if (stage.Asset == Names[0]) VerifyUi(changed[patchUiPath], stage);
            }
        }
        catch
        {
            foreach (var file in files) File.WriteAllBytes(file, originals[file]);
            using var cas = new FileStream(target.FullName, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None);
            cas.SetLength(originalCasLength);
            cas.Flush(flushToDisk: true);
            throw;
        }
        Console.WriteLine($"Installed {stages.Count} enlarged GFX fonts; CAS/CAT/UI backups: {backupDirectory}");
        Console.WriteLine("Battlefield 4 was not launched.");
    }

    public static void Restore(string backupDirectory)
    {
        TextInstaller.EnsureStopped();
        var backup = JsonSerializer.Deserialize<FontBackup>(
            File.ReadAllText(Path.Combine(backupDirectory, "font_backup.json")))
            ?? throw new InvalidDataException("Invalid font backup manifest");
        var originals = new List<(string Path, byte[] Bytes)>();
        foreach (var file in backup.Files)
        {
            var bytes = File.ReadAllBytes(Path.Combine(backupDirectory, file.Name));
            if (!Sha256(bytes).Equals(file.OriginalSha256, StringComparison.OrdinalIgnoreCase) ||
                !Sha256(File.ReadAllBytes(file.Path)).Equals(file.InstalledSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Font metadata changed since installation: {file.Path}");
            originals.Add((file.Path, bytes));
        }
        using (var cas = File.OpenRead(backup.CasPath))
        {
            var length = checked((int)(cas.Length - backup.OriginalCasLength));
            if (length < 0) throw new InvalidDataException("CAS archive is shorter than backup");
            var bytes = new byte[length];
            cas.Position = backup.OriginalCasLength;
            cas.ReadExactly(bytes);
            if (!Sha256(bytes).Equals(backup.AppendedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Appended CAS bytes changed since installation");
        }
        foreach (var file in originals) File.WriteAllBytes(file.Path, file.Bytes);
        using (var cas = new FileStream(backup.CasPath, FileMode.Open,
                   FileAccess.ReadWrite, FileShare.None))
        {
            cas.SetLength(backup.OriginalCasLength);
            cas.Flush(flushToDisk: true);
        }
        Console.WriteLine("Restored original font CAT/UI files and truncated appended CAS bytes");
    }

    private static DbNode FindResource(DbNode ui, string name)
    {
        var matches = (ui.Child("bundles")?.Children ?? [])
            .SelectMany(bundle => bundle.Child("res")?.Children ?? [])
            .Where(asset => asset.Text("name") == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"Expected one UI font asset: {name}");
        return matches[0];
    }

    private static void PatchUi(byte[] uiBytes, FontStage stage, byte[] newSha, bool required)
    {
        var ui = FrostbiteDb.Read(uiBytes);
        var matches = (ui.Child("bundles")?.Children ?? [])
            .SelectMany(bundle => bundle.Child("res")?.Children ?? [])
            .Where(asset => asset.Text("name") == stage.Asset).ToArray();
        if (matches.Length == 0 && !required) return;
        if (matches.Length != 1) throw new InvalidDataException($"Unexpected UI reference count: {stage.Asset}");
        var asset = matches[0];
        var shaNode = asset.Child("sha1")!;
        var sizeNode = asset.Child("size")!;
        var originalNode = asset.Child("originalSize")!;
        if (shaNode.Type != 16 || sizeNode.Type != 9 || originalNode.Type != 9 ||
            !((byte[])shaNode.Value!).AsSpan().SequenceEqual(Convert.FromHexString(stage.OldSha1)) ||
            sizeNode.Value is not ulong packed || packed != (ulong)stage.OldPackedSize ||
            originalNode.Value is not ulong decoded || decoded != (ulong)stage.OldDecodedSize)
            throw new InvalidDataException($"Unexpected UI font metadata: {stage.Asset}");
        newSha.CopyTo(uiBytes.AsSpan(shaNode.ValueOffset, 20));
        BinaryPrimitives.WriteUInt64LittleEndian(uiBytes.AsSpan(sizeNode.ValueOffset, 8),
            checked((ulong)stage.NewPackedSize));
        BinaryPrimitives.WriteUInt64LittleEndian(uiBytes.AsSpan(originalNode.ValueOffset, 8),
            checked((ulong)stage.NewDecodedSize));
    }

    private static void PatchCatalog(byte[] catalog, CasEntry source, byte[] oldSha,
        byte[] newSha, uint newOffset, uint newSize, uint archiveNumber)
    {
        if (!catalog.AsSpan(0, 16).SequenceEqual("NyanNyanNyanNyan"u8) ||
            (catalog.Length - 16) % 32 != 0)
            throw new InvalidDataException("Unexpected BF4 CAS catalog layout");
        var matches = 0;
        for (var at = 16; at < catalog.Length; at += 32)
        {
            if (!catalog.AsSpan(at, 20).SequenceEqual(oldSha)) continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(catalog.AsSpan(at + 20)) != source.Offset ||
                BinaryPrimitives.ReadUInt32LittleEndian(catalog.AsSpan(at + 24)) != source.Size)
                throw new InvalidDataException("Original catalog record changed");
            newSha.CopyTo(catalog.AsSpan(at, 20));
            BinaryPrimitives.WriteUInt32LittleEndian(catalog.AsSpan(at + 20), newOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(catalog.AsSpan(at + 24), newSize);
            BinaryPrimitives.WriteUInt32LittleEndian(catalog.AsSpan(at + 28), archiveNumber);
            matches++;
        }
        if (matches != 1) throw new InvalidDataException($"Expected one CAT record, found {matches}");
    }

    private static void VerifyUi(byte[] uiBytes, FontStage stage)
    {
        var asset = FindResource(FrostbiteDb.Read(uiBytes), stage.Asset);
        if (!((byte[])asset.Child("sha1")!.Value!).AsSpan()
                .SequenceEqual(Convert.FromHexString(stage.NewSha1)) ||
            asset.Number("size") != (ulong)stage.NewPackedSize ||
            asset.Number("originalSize") != (ulong)stage.NewDecodedSize)
            throw new InvalidDataException($"Installed UI metadata mismatch: {stage.Asset}");
    }

    private static void ValidateGfx(byte[] gfx, string name)
    {
        if (gfx.Length < 8 || !gfx.AsSpan().StartsWith("GFX"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(gfx.AsSpan(4)) != gfx.Length)
            throw new InvalidDataException($"Invalid GFX length or signature: {name}");
    }

    private static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
