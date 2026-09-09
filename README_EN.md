# Drama Merger (短剧合并工具)

[中文](README.md) | [English](README_EN.md)

A **Windows desktop tool** that losslessly merges episode-based videos (short dramas, series) scattered across folders into a single file — with **incremental updates**: when new episodes are downloaded later, just run it again and they are appended to the existing merged file, whose name updates automatically (e.g. `EP1-10` → `EP1-12`).

Built on .NET 8 (WinForms) + ffmpeg lossless concat (`-c copy`, no re-encoding) — merging 1 GB of footage typically takes seconds.

## ✨ Features

- 📺 **Automatic episode detection**: parses episode numbers from `第01集` / `EP01` patterns in file names, sorted numerically (no more "EP10 before EP2" problems)
- 🔗 **Lossless merging**: ffmpeg `-c copy` stream copy — no re-encoding, no quality loss, very fast
- 🌐 **Bilingual UI (中文 / English)**: one click in the top-right corner, choice is remembered
- 📈 **Incremental updates**: a hidden manifest in the output folder tracks merged episodes; after new downloads, re-run to append and rename (`EP1-10` → `EP1-12`); the old file is cleaned up automatically
- 📁 **Multi-folder support**: keep old episodes and new downloads in separate folders — add and check them all, the tool scans across folders
- 🗂️ **Batch mode**: one root folder with one subfolder per drama — merge everything in one go into a single output folder
- 🛡️ **Safety checks**: duplicate episode numbers, non-contiguous episodes, modified source files — all reported clearly before anything happens
- 📊 **Real progress**: total duration read via ffprobe drives the progress bar; merging can be canceled at any time

## 📦 Getting Started

### Requirements

- Windows 10 / 11 (x64)
- [ffmpeg](https://www.gyan.dev/ffmpeg/builds/) (`ffmpeg.exe` and `ffprobe.exe` on PATH, or placed next to this program)

### Usage

1. Run `DramaMerger.exe` (single portable file, no installation)
2. Click "Add folder…" to add the folder(s) containing episodes, and check them
3. Choose an output folder, click "Scan & Merge"
4. Confirm the plan and wait

### Incremental updates (appending episodes)

After merging episodes 1–10, you download episodes 11–12:

1. Put the new files into any checked episode folder (or add a new folder)
2. Click "Scan & Merge" again
3. The tool detects the new episodes, appends them to the existing file, and the output becomes `DramaName EP1-12.mp4`; the old `EP1-10` file is removed

### Batch mode

```
D:\Dramas\
├─ DramaA\       ← EP01 ~ EP10.mp4
├─ DramaB\       ← EP01 ~ EP08.mp4
└─ DramaC\
```

Check "Batch mode (each subfolder = one drama)", select the root folder, and merge all dramas into one output folder. Manifests are tracked per drama — when one drama gets new episodes later, only that one is re-processed.

## 🔢 Episode number parsing rules

| File name example | Parsed episode |
|---|---|
| `被潜规则的女孩 第07集.mp4` | 7 |
| `Some Drama EP05.mp4` | 5 |
| `Show Episode 12.mp4` | 12 |
| `Special SP.mp4` (no number) | skipped |
| `DramaName 第1-10集.mp4` (merged output) | skipped (never self-detected) |

Output naming: Chinese UI `DramaName 第1-10集.mp4`, English UI `DramaName EP1-10.mp4`.

A merged file placed back into an episode folder is never mistaken for an episode.

## 🛠️ Building from source

```bash
git clone https://github.com/dianziboluo/DramaMerger.git
cd DramaMerger
dotnet build -c Release
dotnet publish -c Release   # single-file exe at bin\Release\net8.0-windows\win-x64\publish\
```

Requires the .NET 8 SDK ([download](https://dotnet.microsoft.com/download/dotnet/8.0)).

## ⚙️ How it works

1. **Scan**: `Directory.EnumerateFiles` + regex episode parsing, numeric sort, deduped across all checked folders
2. **Plan**: reads the merge manifest (`.merge-manifest-<drama>.json`) in the output folder, compares current files, and decides between full merge / incremental append / up-to-date / missing episodes / source changed
3. **Merge**: writes an ffmpeg concat list → `ffmpeg -f concat -safe 0 -c copy -movflags +faststart` → writes to a temp file first, then atomically replaces the output
4. **Manifest update**: records merged file names and sizes for the next incremental run

> Lossless concat requires all episodes to share compatible encoding parameters (usually true within one show). If ffmpeg fails, the full error is shown in the log.

## ❓ FAQ

**Q: Does it re-encode / lose quality?**
No. `-c copy` copies the video/audio streams as-is; only the container is rewritten.

**Q: What is the manifest file? Can I delete it?**
`.merge-manifest-<drama>.json` in the output folder tracks what has been merged. Deleting it just forces a full re-merge — no data is lost.

**Q: "Missing episode X" warning?**
Episodes must be contiguous to guarantee watch order. Add the missing file and retry.

**Q: "Multiple files for episode X"?**
The same episode number appears twice (e.g. a duplicate download). Remove one and retry.

## 📄 License

[MIT](LICENSE)
