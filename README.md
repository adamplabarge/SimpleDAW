# SimpleDAW

SimpleDAW is a lightweight digital audio workstation built with WPF and .NET 8.

## Screenshot

![SimpleDAW Screenshot](SimpleDAW.png)

## Features

- Multi-track playback and audio routing
- Live input support
- Waveform visualization
- MIDI timing/clock support

## Build

```bash
dotnet restore SimpleDAW.csproj
dotnet build SimpleDAW.csproj -c Release
```

## Release Installer

This repository includes GitHub Actions workflow automation to build a Windows installer on version tags (`v*`).
