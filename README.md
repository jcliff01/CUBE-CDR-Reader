# CUBE CDR Reader

A C# Windows desktop reader for Cisco Unified Border Element file-accounting records. Open a single log or load the supported files in a folder. The reader opens logs read-only and displays one row per accounting record. A call can have multiple records.

## Supported files

- Cisco headerless detailed and compact VoIP file-accounting records.
- Named CSV exports with headings such as `h323-setup-time`, `clid`, `dnis`, and `duration-seconds`.
- Folder import scans the top level for CSV, CDR, LOG, TXT, extensionless files and filenames with numeric suffixes. The status bar shows file errors, skipped rows, and the import limit of 250,000 records.

The reader uses explicit duration in seconds when available. Otherwise it calculates connected duration from connect and disconnect timestamps when their zone labels match. Setup filters use the logged clock values. The upper setup bound includes the entire selected second.

## Filters

The twelve controls are setup from, setup through, minimum duration, maximum duration, calling number, called number, codec, cause code, disconnected by, remote signaling IP, call ID, and forwarded status. **Apply filters** requires all enabled controls to match. Text filters match substrings without regard to case. Forwarded status matches the selected status exactly. Click a column heading to sort and **Clear filters** to reset the form.

## Build

Install the .NET 8 SDK and run:

```powershell
dotnet publish CubeCdrReader.csproj -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Run `publish/CUBE-CDR-Reader.exe` on Windows x64. The published executable includes the .NET Desktop Runtime.

[Cisco IOS Voice Gateway CDR accounting reference](https://www.cisco.com/c/en/us/td/docs/ios/voice/cdr/developer/manual/cdrdev.pdf)
