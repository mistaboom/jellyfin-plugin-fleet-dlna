# Fleet DLNA

Fleet DLNA is a Jellyfin DLNA server plugin optimized for groups of Samsung and LG televisions. It keeps Jellyfin's device-profile-driven playback while providing fast, predictable navigation for large movie and TV libraries.

## Features

- Alphabetical movie and series browsing
- Latest and genre views scoped to the current library
- Bounded result pages and parallel stream planning for responsive televisions
- Jellyfin device profiles, transcoding, Play To, and SSDP discovery
- Separate plugin identity, so it does not replace the official Jellyfin DLNA plugin

## Compatibility

| Fleet DLNA | Jellyfin | Runtime |
| --- | --- | --- |
| 2.x | 12.x | .NET 10 |
| 1.x | 10.11.x | .NET 9 |

## Installation

Add `https://raw.githubusercontent.com/mistaboom/jellyfin-plugin-fleet-dlna/master/manifest.json` as a Jellyfin plugin catalog, or download the release ZIP and extract it into a `Fleet DLNA` directory under Jellyfin's plugins directory. Restart Jellyfin after installation or upgrade.

## Development

```powershell
dotnet restore Jellyfin.Plugin.Dlna.slnx
dotnet build Jellyfin.Plugin.Dlna.slnx
```

Fleet DLNA is based on the official [Jellyfin DLNA plugin](https://github.com/jellyfin/jellyfin-plugin-dlna) and is licensed under GPL-3.0.
