# Moz VPN :banana:

A banana from hell, for connecting to the internet in Iran

We really don't feel like explaining it, but the important thing is that it's safe and it works...

## Building Moz_Avalonia

Install the .NET 10 SDK, then run these commands from the repository root.

Build and run the desktop application locally:

```sh
dotnet build Moz_Avalonia/Moz_Avalonia.csproj -c Release
dotnet run --project Moz_Avalonia/Moz_Avalonia.csproj -c Release
```

Publish a self-contained, single-file Linux x64 executable:

```sh
dotnet publish Moz_Avalonia/Moz_Avalonia.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishAot=false -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/Moz_Avalonia/linux-x64
```

Publish a self-contained, single-file Windows x64 executable:

```sh
dotnet publish Moz_Avalonia/Moz_Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishAot=false -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/Moz_Avalonia/win-x64
```

The resulting applications are:

- `artifacts/Moz_Avalonia/linux-x64/Moz_Avalonia`
- `artifacts/Moz_Avalonia/win-x64/Moz_Avalonia.exe`

Trimming and NativeAOT are intentionally disabled. The current application and networking dependencies use reflection without complete trimming annotations, so enabling either optimization is not considered safe yet.

See [`Moz_Avalonia/README.md`](Moz_Avalonia/README.md) for architecture and usage details.
