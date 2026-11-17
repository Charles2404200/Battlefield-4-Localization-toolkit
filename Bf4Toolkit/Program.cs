namespace Bf4Toolkit;

internal sealed record GameLayer(string Name, string Root);

internal static class Program
{
    private const string DefaultGame =
        @"C:\Program Files (x86)\Steam\steamapps\common\Battlefield 4";

    internal static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "-h" or "--help")
            {
                Console.WriteLine("BF4 toolkit: inspect|export-all|export-text|export-fonts|export-gfx GAME_ROOT OUTPUT_DIRECTORY");
                Console.WriteLine("               export-sheet GAME_ROOT OUTPUT_CSV");
                Console.WriteLine("               stage-text GAME_ROOT TRANSLATIONS_CSV OUTPUT_DIRECTORY");
                Console.WriteLine("               stage-fonts GAME_ROOT EDITED_FONT_DIRECTORY OUTPUT_DIRECTORY");
                Console.WriteLine("               install-text|install-fonts GAME_ROOT STAGE_DIRECTORY BACKUP_DIRECTORY");
                Console.WriteLine("               restore-text|restore-fonts GAME_ROOT BACKUP_DIRECTORY");
                Console.WriteLine($"Default GAME_ROOT: {DefaultGame}");
                return args.Length == 0 ? 1 : 0;
            }
            var game = Path.GetFullPath(args.Length > 1 ? args[1] : DefaultGame);
            var output = Path.GetFullPath(args.Length > 2 ? args[2] : "dump");
            if (!File.Exists(Path.Combine(game, "bf4.exe")))
                throw new FileNotFoundException("BF4 game root not found", game);
            var layers = new[]
            {
                new GameLayer("data", Path.Combine(game, "Data")),
                new GameLayer("patch", Path.Combine(game, "Update", "Patch", "Data"))
            };
            switch (args[0])
            {
                case "inspect": Inspect(layers); break;
                case "stage-text":
                    if (args.Length < 4) throw new ArgumentException("stage-text requires GAME_ROOT CSV OUTPUT_DIRECTORY");
                    TextImporter.Stage(layers[1], args[2], Path.GetFullPath(args[3]));
                    break;
                case "stage-fonts":
                    if (args.Length < 4) throw new ArgumentException("stage-fonts requires GAME_ROOT FONT_DIRECTORY OUTPUT_DIRECTORY");
                    FontImporter.Stage(layers[0], layers[1], args[2], Path.GetFullPath(args[3]));
                    break;
                case "install-text":
                    if (args.Length < 4) throw new ArgumentException("install-text requires GAME_ROOT STAGE_DIRECTORY BACKUP_DIRECTORY");
                    TextInstaller.Install(layers[1], args[2], Path.GetFullPath(args[3]));
                    break;
                case "install-fonts":
                    if (args.Length < 4) throw new ArgumentException("install-fonts requires GAME_ROOT STAGE_DIRECTORY BACKUP_DIRECTORY");
                    FontImporter.Install(layers[0], layers[1], args[2], Path.GetFullPath(args[3]));
                    break;
                case "restore-text":
                    if (args.Length < 3) throw new ArgumentException("restore-text requires GAME_ROOT BACKUP_DIRECTORY");
                    TextInstaller.Restore(args[2]);
                    break;
                case "restore-fonts":
                    if (args.Length < 3) throw new ArgumentException("restore-fonts requires GAME_ROOT BACKUP_DIRECTORY");
                    FontImporter.Restore(args[2]);
                    break;
                case "export-all":
                    TextExporter.Run(layers, Path.Combine(output, "text"));
                    FontExporter.Run(layers, Path.Combine(output, "fonts"));
                    break;
                case "export-sheet":
                    if (args.Length < 3) throw new ArgumentException("export-sheet requires GAME_ROOT OUTPUT_CSV");
                    TextExporter.ExportSheet(layers[1], args[2]);
                    break;
                case "export-text": TextExporter.Run(layers, output); break;
                case "export-fonts": FontExporter.Run(layers, output); break;
                case "export-gfx": FontExporter.Run(layers, output, includeWebFonts: false); break;
                default: throw new ArgumentException($"Unknown command: {args[0]}");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
                                   ArgumentException or OverflowException or
                                   UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static void Inspect(IEnumerable<GameLayer> layers)
    {
        foreach (var layer in layers)
        {
            var toc = FrostbiteDb.ReadSigned(Path.Combine(layer.Root, "Win32", "Loc", "en.toc"));
            var chunks = toc.Child("chunks")?.Children ??
                throw new InvalidDataException($"{layer.Name} en.toc has no chunks");
            Console.WriteLine($"{layer.Name} localization chunks:");
            for (var i = 0; i < chunks.Count; i++)
                Console.WriteLine($"  {i}: en.sb+0x{chunks[i].Number("offset")!.Value:X} " +
                    $"packed={chunks[i].Number("size")!.Value:N0} bytes");
            var ui = FrostbiteDb.Read(Path.Combine(layer.Root, "Win32", "UI.sb"));
            foreach (var bundle in ui.Child("bundles")?.Children ?? [])
                if (bundle.Text("path")?.Contains("fontcollection_en_",
                        StringComparison.OrdinalIgnoreCase) == true)
                {
                    Console.WriteLine($"  font bundle: {bundle.Text("path")}");
                    foreach (var asset in bundle.Child("res")?.Children ?? [])
                    {
                        var sha = asset.Child("sha1");
                        Console.WriteLine($"    {asset.Text("name")}: sha type={sha?.Type} at 0x{sha?.ValueOffset:X}, " +
                            $"size type={asset.Child("size")?.Type} at 0x{asset.Child("size")?.ValueOffset:X}, " +
                            $"originalSize type={asset.Child("originalSize")?.Type} at 0x{asset.Child("originalSize")?.ValueOffset:X}");
                    }
                }
        }
    }
}
