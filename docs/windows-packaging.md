# Windows packaging notes (exe metadata, icon, signing)

Hard-won details about how the published `VolumeRelay.exe` is produced and how to
verify it. Everything here was measured on .NET SDK 8.0.4xx / Windows 11. Read this
before changing version metadata, the icon, or the publish format — and before
"verifying" a build.

## Which project properties actually reach the exe

`Microsoft.NET.Sdk` generates `obj/…/<Project>.AssemblyInfo.cs` from these
properties, and the apphost's Win32 version resource is built from the resulting
**assembly attributes** (`Microsoft.NET.GenerateAssemblyInfo.targets`):

| Exe field (Properties → Details) | MSBuild property | Assembly attribute |
|---|---|---|
| Company | `AssemblyCompany` | `AssemblyCompanyAttribute` |
| Product name | `Product` | `AssemblyProductAttribute` |
| **File description** | **`AssemblyTitle`** | `AssemblyTitleAttribute` |
| File version | `Version` / `FileVersion` | `AssemblyFileVersionAttribute` |
| Product version | `Version` | `AssemblyInformationalVersionAttribute` |
| Copyright | `Copyright` | `AssemblyCopyrightAttribute` |
| Comments | `Description` | `AssemblyDescriptionAttribute` |

Traps that cost real time:

- **`<AssemblyDescription>` and `<AssemblyCopyright>` do not exist.** They look
  symmetrical with `AssemblyCompany`/`AssemblyTitle` but the SDK ignores them —
  they are inert properties, no error, no warning. The real names are bare
  `Description` and `Copyright`.
- **File description comes from `AssemblyTitle`, not `Description`.** `AssemblyTitle`
  defaults to `$(AssemblyName)`, which is why an untouched project shows its own
  name in the Description column no matter what `Description` says.
- **Non-ASCII is silently dropped.** `<Copyright>© 2026 bubblec0re</Copyright>`
  produced an *empty* copyright field; `Copyright (c) 2026 bubblec0re` works.
  Assume ASCII-only for every value that must appear in the exe's version block.
- **`LegalCopyright` never appears in the single-file exe.** The plain apphost
  (`obj/…/win-x64/apphost.exe`) does get it; the `singlefilehost.exe` intermediate
  and therefore `dist/VolumeRelay.exe` do not — verified independent of the string
  contents, so it is a bundler/SDK behaviour, not a configuration mistake. Keep
  `<Copyright>` anyway (it lands in the assembly metadata and will show up if the
  SDK ever propagates it), but do not treat "copyright is empty" as a bug to chase.

## Incremental builds will lie to you

Editing metadata in the csproj does **not** reliably regenerate `AssemblyInfo.cs` —
the target's up-to-date check is driven by a hash cache
(`obj/…/<Project>.AssemblyInfoInputs.cache`) and only runs when the compiler runs.
A build that says "succeeded" in 0.5 s may have compiled nothing at all.

Also note the obj folder is **per RID**:

- `dotnet build` (no `-r`) → `obj/Release/net8.0-windows/`
- `dotnet build/publish -r win-x64` → `obj/Release/net8.0-windows/win-x64/`

Publish uses the `win-x64` one, so a plain rebuild does not refresh what you then
publish, and the exe you inspect can be older than the csproj you just edited.

When metadata "doesn't take":

```powershell
Remove-Item VolumeRelay\obj\Release\net8.0-windows\win-x64\VolumeRelay.AssemblyInfo.cs,
            VolumeRelay\obj\Release\net8.0-windows\win-x64\VolumeRelay.AssemblyInfoInputs.cache
dotnet publish VolumeRelay/VolumeRelay.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist
```

Or confirm what the build *thinks* the properties are, without guessing:

```powershell
dotnet msbuild VolumeRelay/VolumeRelay.csproj -getProperty:Description -getProperty:Copyright `
  -getProperty:AssemblyTitle -getProperty:AssemblyCompany -getProperty:Product
```

## Verifying the published exe

Use the Windows APIs — they are what Explorer's Properties dialog and the taskbar
render, so they are the ground truth:

```powershell
$p = 'dist\VolumeRelay.exe'
(Get-Item $p).VersionInfo |
  Select-Object CompanyName, ProductName, FileDescription, LegalCopyright, FileVersion |
  Format-List
[System.Drawing.Icon]::ExtractAssociatedIcon($p) | Select-Object Width, Height
Get-FileHash $p -Algorithm SHA256
```

Do **not** hand-roll a PE/resource parser for this (that attempt burned hours and
produced confident nonsense). If you ever do walk the resource directory, the
offsets are: `e_lfanew` at `0x3C`, COFF header at `e_lfanew+4`,
`NumberOfSections` at `coff+2`, `NumberOfSymbols` at `coff+12`,
**`SizeOfOptionalHeader` at `coff+16`**, optional header at `coff+20` (data
directories at `opt+112` for PE32+, `opt+96` for PE32). Off-by-two here yields
plausible-looking garbage rather than an error.

Also: verify **the artifact you just built**, not whatever is in the tray. The
single-instance mutex means a stale instance (or a downloaded copy the user is
running) stays alive and keeps "proving" that the old exe works. Kill it, launch
`dist\VolumeRelay.exe`, and check `%TEMP%\volumerelay-crash.log` afterwards.

## Icon

`VolumeRelay/VolumeRelay.ico` is referenced twice on purpose:

- `<ApplicationIcon>` — bakes the icon into the exe's Win32 resources (Explorer,
  SmartScreen dialog, file properties).
- `<EmbeddedResource>` — lets `TrayApp.CreateIcon()` load the same file at runtime;
  the single-file publish has no loose files next to the exe, so a disk load is not
  an option. The resource is found by its `.ico` suffix, so renaming the file is fine,
  changing the extension is not.

## SmartScreen

The release is unsigned and self-contained single-file, so Windows treats it as an
anonymous, freshly-extracted packer and shows "unrecognized app". That framing
cannot be fixed here: self-signed certificates give zero reputation, EV certificates
no longer bypass SmartScreen, and code signing is not available for this project.
The only free wins — applied — are real identity metadata, a real icon, a published
SHA-256, and a README note telling users how to verify and proceed. Do not "fix" the
warning by switching to a framework-dependent build (target users have no .NET
desktop runtime) or by adding a zip (declined; the link format must stay a single
`VolumeRelay.exe`).
