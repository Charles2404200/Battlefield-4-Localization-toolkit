# Battlefield 4 Localization Toolkit

A Windows toolkit for exporting and importing Battlefield 4 localization text and Scaleform fonts. The GUI and command-line tools are written in C# and work with the installed game's files. They never launch Battlefield 4.

This repository contains source code only. It does not contain game archives, extracted fonts, translations, backups, or a prebuilt patch.

## Requirements and build

- Windows with Battlefield 4 installed (developed against the Steam edition).
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build from source.

Build and run the Windows Forms GUI:

```powershell
dotnet build .\Bf4Gui\Bf4Gui.csproj -c Release
dotnet run --project .\Bf4Gui\Bf4Gui.csproj -c Release
```

To publish a standalone Windows executable that bundles .NET:

```powershell
dotnet publish .\Bf4Gui\Bf4Gui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

Run `publish\Bf4Gui.exe`. Build and publish output is generated locally and is not tracked in Git.

## GUI workflow

The GUI has four paths: the BF4 game directory, a translation CSV, a font directory, and a workspace for exports, stages, and backups. Use **Chọn...** to change a path. Your choices are remembered between runs.

1. Click **Xuất text CSV** to export the active Patch localization to an editable UTF-8 `ID,Text` sheet. The new CSV is selected automatically.
2. Click **Xuất 3 font GFX** to export the three UI font collections. Their import-ready directory is selected automatically.
3. Edit the `Text` column or exported `.gfx` files. Keep CSV IDs and font paths intact.
4. Exit Battlefield 4. Click **Cài text** or **Cài 3 font GFX**. Each action validates its input, stages a replacement, makes a separate backup, and installs that asset type.
5. To undo an installation, select its backup on the right and click **Khôi phục text** or **Khôi phục font**. Restore multiple installations in reverse order.

The log displays progress and errors. The GUI refuses to install or restore while `bf4.exe` or `bf4_x86.exe` is running. It does not start the game.

### Translation CSV

The editable sheet has two columns:

```csv
ID,Text
0x12345678,"A sentence, with a comma"
```

Keep every exported ID and edit only `Text`. The Patch table has 17,046 records and 17,044 distinct IDs; repeated IDs share one CSV value. An empty `Text` cell keeps the text currently installed for that ID. Use standard CSV quoting for commas, quotes, and line breaks: `""` represents a literal quote inside a quoted cell.

The importer reports unquoted commas that it joins back into the Text column. It can also recover an unmatched quote when the next physical line starts with a complete `0x` ID. It does not change the source CSV. Missing, extra, malformed, or duplicate IDs still stop the import.

### Font directory

Edit these three files under the exported directory and retain their paths:

```text
data/ui/static/fontcollection_en_fontlibwin32.gfx
data/ui/static/ondemandfontcollection/eulafontcollection_en_fontlibwin32.gfx
data/ui/static/ondemandfontcollection/storefontcollection_en_fontlibwin32.gfx
```

The importer supports enlarged GFX files. The CLI's full font export also extracts WebBrowser EBX/TTF assets for research; the GUI imports only the three GFX collections above.

## Command-line interface

Use your own game and translation paths:

```powershell
$game = 'C:\Program Files (x86)\Steam\steamapps\common\Battlefield 4'
$csv = 'C:\path\to\translation.csv'
$fonts = 'C:\path\to\exported-fonts'

dotnet run --project .\Bf4Toolkit\Bf4Toolkit.csproj -c Release -- export-sheet $game .\exports\bf4-text.csv
dotnet run --project .\Bf4Toolkit\Bf4Toolkit.csproj -c Release -- export-gfx $game .\exports\bf4-fonts

dotnet run --project .\Bf4Toolkit\Bf4Toolkit.csproj -c Release -- stage-text $game $csv .\stages\text-1
dotnet run --project .\Bf4Toolkit\Bf4Toolkit.csproj -c Release -- stage-fonts $game $fonts .\stages\fonts-1

dotnet run --project .\Bf4Toolkit\Bf4Toolkit.csproj -c Release -- install-text $game .\stages\text-1 .\backups\text-1
dotnet run --project .\Bf4Toolkit\Bf4Toolkit.csproj -c Release -- install-fonts $game .\stages\fonts-1 .\backups\fonts-1

dotnet run --project .\Bf4Toolkit\Bf4Toolkit.csproj -c Release -- restore-text $game .\backups\text-1
dotnet run --project .\Bf4Toolkit\Bf4Toolkit.csproj -c Release -- restore-fonts $game .\backups\fonts-1
```

Use new stage and backup directories for each installation. Staging reads the game but does not change it. The CLI also provides `inspect`, `export-text`, `export-fonts`, and `export-all` for detailed research exports; run it with `--help` for the argument list.

## Installation and limits

The text importer reads `Update\Patch\Data\Win32\Loc\en.sb`. It rebuilds the string table and Unicode histogram, verifies that every ID decodes to the requested text, and encodes both CAS chunks at exactly their existing packed and decoded sizes. It leaves the signed `en.toc` untouched. If the replacement cannot fit, staging stops before writing a game file.

The font importer finds its assets through `Data\Win32\UI.sb` and `Data\cas.cat`. It validates each edited GFX, appends compressed payloads to a Data CAS archive, updates the catalog and affected base/Patch UI bundles, and reads the installed assets back to verify them. The selected `cas_*.cas` archive can differ between installations.

Each installation stores byte-for-byte originals in its backup directory. Restore checks the hashes of the installed files before replacing them. The game's fixed chunk capacities and address fields still impose limits; the toolkit reports an error if a translation or font cannot be represented safely.

## Repository layout

```text
Bf4Gui/      Windows Forms interface
Bf4Toolkit/  Frostbite DB, CAS, localization, font, staging, and restore code
README.md    User and technical documentation
```

The Frostbite DB interpretation was informed by the [Frostbite-Scripts BF4 parser](https://github.com/NicknineTheEagle/Frostbite-Scripts/blob/master/frostbite3/dbo.py). Game offsets and asset paths were checked against an installed BF4 copy. Local dumps and extracted game assets are intentionally excluded from this repository.
