using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bf4Toolkit;

internal sealed record TextBackup(string GameFile, string OriginalSha256,
    string InstalledSha256, string BackupFile);

internal static class TextInstaller
{
    public static void EnsureStopped()
    {
        if (Process.GetProcessesByName("bf4").Length != 0 ||
            Process.GetProcessesByName("bf4_x86").Length != 0)
            throw new IOException("Exit Battlefield 4 before installing or restoring files");
    }

    public static void Install(GameLayer patch, string stageDirectory, string backupDirectory)
    {
        EnsureStopped();
        if (Directory.Exists(backupDirectory) || File.Exists(backupDirectory))
            throw new IOException($"Backup path already exists: {backupDirectory}");
        var sbPath = Path.Combine(patch.Root, "Win32", "Loc", "en.sb");
        var toc = FrostbiteDb.ReadSigned(Path.Combine(patch.Root, "Win32", "Loc", "en.toc"));
        var chunks = toc.Child("chunks")?.Children ?? throw new InvalidDataException("No localization chunks");
        if (chunks.Count != 2) throw new InvalidDataException("Expected two localization chunks");
        using var stage = JsonDocument.Parse(File.ReadAllText(Path.Combine(stageDirectory, "text_stage.json")));
        var original = File.ReadAllBytes(sbPath);
        var modified = original.ToArray();
        var decoded = new List<byte[]>();
        for (var i = 0; i < 2; i++)
        {
            var name = i == 0 ? "patch_en_text" : "patch_en_histogram";
            var offset = checked((int)chunks[i].Number("offset")!.Value);
            var size = checked((int)chunks[i].Number("size")!.Value);
            var oldDecoded = CasBlocks.Decode(sbPath, offset, size).Data;
            var oldKey = i == 0 ? "originalTextSha256" : "originalHistogramSha256";
            if (!HashEquals(oldDecoded, stage.RootElement.GetProperty(oldKey).GetString()!))
                throw new InvalidDataException($"Game localization changed since staging: {name}");
            var packedPath = Path.Combine(stageDirectory, name + ".cas-payload");
            var packed = File.ReadAllBytes(packedPath);
            if (packed.Length != size) throw new InvalidDataException($"Wrong packed size: {name}");
            var packedKey = i == 0 ? "textPackedSha256" : "histogramPackedSha256";
            if (!HashEquals(packed, stage.RootElement.GetProperty(packedKey).GetString()!))
                throw new InvalidDataException($"Staged payload changed: {name}");
            var newDecoded = CasBlocks.Decode(packedPath, 0, packed.Length).Data;
            var decodedKey = i == 0 ? "textSha256" : "histogramSha256";
            if (!HashEquals(newDecoded, stage.RootElement.GetProperty(decodedKey).GetString()!))
                throw new InvalidDataException($"Staged CAS round-trip failed: {name}");
            decoded.Add(newDecoded);
            packed.CopyTo(modified, offset);
        }
        Localization.Read(decoded[0], decoded[1]);
        var backup = new TextBackup(sbPath, Hex(original), Hex(modified), "en.sb.original");
        Directory.CreateDirectory(backupDirectory);
        File.WriteAllBytes(Path.Combine(backupDirectory, backup.BackupFile), original);
        File.WriteAllText(Path.Combine(backupDirectory, "text_backup.json"),
            JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        try
        {
            File.WriteAllBytes(sbPath, modified);
            if (!HashEquals(File.ReadAllBytes(sbPath), backup.InstalledSha256))
                throw new IOException("Installed en.sb readback differs");
        }
        catch { File.WriteAllBytes(sbPath, original); throw; }
        Console.WriteLine("Installed translated Patch en.sb; en.toc is unchanged. Game was not launched.");
    }

    public static void Restore(string backupDirectory)
    {
        EnsureStopped();
        var backup = JsonSerializer.Deserialize<TextBackup>(
            File.ReadAllText(Path.Combine(backupDirectory, "text_backup.json")))
            ?? throw new InvalidDataException("Invalid text backup manifest");
        var original = File.ReadAllBytes(Path.Combine(backupDirectory, backup.BackupFile));
        if (!HashEquals(original, backup.OriginalSha256) ||
            !HashEquals(File.ReadAllBytes(backup.GameFile), backup.InstalledSha256))
            throw new InvalidDataException("Text backup or installed game file changed");
        File.WriteAllBytes(backup.GameFile, original);
        Console.WriteLine("Restored original Patch en.sb");
    }

    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    private static bool HashEquals(byte[] data, string hex) =>
        Hex(data).Equals(hex, StringComparison.OrdinalIgnoreCase);
}
