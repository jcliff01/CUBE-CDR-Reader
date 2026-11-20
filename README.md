# CUBE CDR Reader

A portable Windows desktop viewer for Cisco Unified Border Element SIP call accounting files.

1. Download [CUBE-CDR-Reader.exe](../../raw/main/CUBE-CDR-Reader.exe) to a Windows 10 or 11 x64 PC.
2. Run it and choose **Open Folder**. Select the directory containing your exported CDRs.
3. Search by phone number, codec, IP address, port, forwarding status, call ID, or any other displayed value. Click a column heading to sort. Scroll right for timestamps, call origin, IDs, and source file and line number.

The app runs locally with no installer or network connection. It reads the selected folder's top level and opens input files read-only. It accepts CSV, CDR, LOG, TXT, extensionless files, and Cisco timestamp filenames ending in a numeric suffix. The status bar reports skipped rows, file errors, and when the 250,000-row limit is reached.

## Input and interpretation

- Supported input is one Cisco file accounting record per physical line, using headerless detailed or compact layouts, or a CSV with named fields such as `h323-setup-time`, `clid`, `dnis`, and `codec-type-rate`. The headerless reader targets VoIP legs. For named exports, `duration-seconds` is the preferred explicit duration column.
- Each row is a source record. Multiple records can represent the same call. Use Conference ID and Call ID to investigate related legs and feature records.
- Duration is connected time through disconnect time when both timestamps parse and use the same zone label, or the explicit seconds value in a named export. Times are displayed as recorded and ordered by their local clock values. Missing durations and codecs appear blank.
- The disconnecting party is decoded from a named `release-source` field or an explicit `release-source=value` CSV field. Records without that attribute show **Unknown**. The Cisco cause code is displayed as recorded, alongside the reason text. For example, a raw hexadecimal `10` can appear alongside `normal call clearing (16)`.
- For the widest field coverage, use detailed file accounting. Compact files may omit codec and release source.

[Cisco IOS Voice Gateway CDR accounting reference](https://www.cisco.com/c/en/us/td/docs/ios/voice/cdr/developer/manual/cdrdev.pdf)

- Endpoint columns show signaling and media addresses and ports when recorded. Native detailed files can supply the remote media IP and UDP ports. Local endpoints and SIP signaling ports require explicitly named fields in the export. Missing endpoints appear blank.
- **Forwarded** shows **Yes** or **No** for explicit flags. Cisco forwarding feature records (CFA, CFNA, CFBY) show **Yes** for a successful event or **Forwarding event** when success is unconfirmed. Rows without forwarding evidence show **Unknown**. Forwarded-to displays the destination when supplied. Status applies to the individual record.

## Build

With Zig 0.14.1, run from the repository root. This works on Windows or from a Linux cross-build environment.

```sh
zig cc -target x86_64-windows-gnu -municode -Wl,--subsystem,windows -std=c11 -O2 -s cdr.c main.c -lcomctl32 -lshell32 -lole32 -lgdi32 -o CUBE-CDR-Reader.exe
```

## C# Windows desktop build

The C# edition uses Windows Forms and .NET 8. To run it from source on Windows with the .NET 8 SDK installed:

```powershell
dotnet run --project csharp/CubeCdrReader.csproj
```

To publish a portable Windows x64 executable that includes the .NET runtime:

```powershell
dotnet publish csharp/CubeCdrReader.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-csharp
```

Run `publish-csharp/CUBE-CDR-Reader.exe` and use **Open Folder** to load your accounting files.
