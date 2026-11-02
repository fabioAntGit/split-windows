<p align="center">
  <img src="logo.png" alt="Split" width="220" />
</p>

# Split

Drag files, tap a key, drop them on a wheel of conversions and tools. A private, offline file converter and editor for Windows, living in the notification area.

## Requirements

- Windows 10 22H2 (build 19045) or Windows 11, on x64 or arm64
- Nothing else to install: the program carries its own .NET runtime
- Optional free programs for audio, video, metadata removal and some documents (see [Optional engines](#optional-engines))

## Install

1. Download `Split-<version>-x64-Setup.exe` (or `-arm64-` on an ARM PC) and, if you like, check it against the `.sha256` file next to it:

   ```powershell
   (Get-FileHash .\Split-1.0.0-x64-Setup.exe).Hash.ToLower()
   ```

2. Run it. It installs for you only, in `%LOCALAPPDATA%\Programs\Split`, and never asks for an administrator. The installer isn't code-signed, so SmartScreen warns the first time: click **More info**, then **Run anyway**. This is needed only once.
3. The setup adds **Split** to the Start menu and to **Send to**, and **Open with Split** to the right-click menu of files and folders (on Windows 11 it is under **Show more options**). Two options are off by default: a desktop icon and **Add splitapp to PATH**.
4. Optional: install the free engines below.

To uninstall, use **Settings › Apps** (or the uninstaller in the install folder). It removes the program, the menu entries, the Send to shortcut, the PATH entry and the start-at-login value. Your settings in `%APPDATA%\Split` stay.

## Use

1. Start dragging files from File Explorer, the desktop or a mail window.
2. While still dragging, tap a key once:
   - **Shift**: the **conversion** wheel (formats)
   - **Ctrl**: the **tools** wheel (editors, compress, PDF, sound…)
   - **Alt**: the **actions** wheel (shelf, copy, copy paths, move, terminal)
3. Drop on the slice you want. The result is saved next to the original, which is never changed.

It also works without dragging: in the notification area, choose **Convert Files…**, **Tools…** or **Actions…** and then the files, or use **Open with Split** or **Send to › Split** in File Explorer. The wheel then follows the mouse (press highlights, release confirms) and the keyboard (arrows, Enter, Esc). Clicking outside closes it. The keys can be changed in **Settings › Shortcuts**. Files selected together arrive as one job.

Windows doesn't hand a drag from a program running as administrator to a normal program, so drag from a normal window.

### Groups

Slices that do similar things share one slice with three dots on its rim, for example **Compress** or **Sound**. Click it, or rest on it for half a second while dragging, and it opens as a wheel of its own. **Esc**, **Backspace** or the centre go back.

### Settings › Wheels

The preview is the wheel itself, as saved. Drag a slice to reorder, click it to read what it does and switch it off, select slices and click **Group** to make a group, and **Restore Defaults** to start over. The wheel can be Extra Small to Extra Large, with or without names under the icons, black or white.

## Editors

| Editor | For | What it does |
|---|---|---|
| Crop | Images | Fixed or free proportions, ready-made shapes for social networks, rotate 90°, flip |
| Annotate and Cover | Images | Arrows, lines, shapes, text, numbers, highlighter; blur, pixelate or black out areas; blur faces automatically |
| Adjust | Images | Exposure, contrast, saturation, temperature and filters, with a before/after comparison |
| Add Background | Images | Padding, corners, shadow, gradients, colour or blurred image, output proportion |
| Collage | 2 or more images | Row, column or grid, spacing and background |
| Edit Media | Video and audio | Trim, speed, volume, fades, crop, rotate, cover areas, export a GIF, save frames |
| Organize PDF | PDF | Reorder, rotate, duplicate and delete pages, add other PDFs or images, extract pages |

Editors save a copy next to the original (for example `photo (cropped).jpg`). Closing with unsaved changes asks first.

## Tools and actions

Compress (image, video, audio and PDF) or compress to a target size, remove metadata, remove background, combine into a PDF, split a PDF, extract archives, create a ZIP, normalize loudness, convert to mono, cut silences, remove audio, join videos, frames, audio to video (waveform MP4) and read QR codes (the text goes to the clipboard).

**Actions:** shelf (keeps files in a floating panel to drop later, or drag them all at once), copy, copy paths, move, and open a terminal in the folder. **Activity…** lists recent jobs and their results.

## What it converts

**Settings › Formats** answers the same per format: choose one to see what it turns into and what it's made from, with a dimmed name when a free program is still missing.

| From | To | Engine |
|---|---|---|
| JPG PNG WebP JPEG XL HEIC AVIF TIFF BMP GIF APNG ICO SVG | JPG PNG WebP JPEG XL AVIF TIFF BMP GIF APNG ICO PDF (HEIC is read only) | Magick.NET, Skia |
| MP3 M4A WAV FLAC OGG Opus AIFF WMA | The same | ffmpeg |
| MP4 MOV M4V MKV WebM AVI WMV | MP4 MOV MKV WebM AVI WMV GIF and every audio format | ffmpeg |
| DOCX ODT RTF TXT HTML Markdown EPUB | PDF DOCX ODT RTF TXT HTML EPUB | Split's own readers and writers |
| DOC, and DOCX with exact layout | PDF | Word or LibreOffice (optional) |
| PDF | PNG JPG TXT DOCX RTF HTML EPUB | PDFium, with Windows OCR for scans |
| CSV TSV XLSX | XLSX CSV TSV HTML PDF JSON | Split's own XLSX reader and writer |
| XLS ODS | XLSX CSV and the rest | Excel or LibreOffice (optional) |
| JSON | CSV TSV XLSX HTML XML YAML | Split's own |
| XML | JSON | Split's own |
| PPTX PPT | PDF | PowerPoint or LibreOffice (optional) |
| SRT VTT | VTT SRT | ffmpeg |
| ZIP TAR TAR.GZ RAR 7Z | ZIP TAR TAR.GZ | SharpCompress |

The XLSX it writes keeps numbers, dates, percentages and formulas, with a bold header, a frozen first row and column widths. When a conversion can't keep something (fonts, comments, animation), the result says what was lost. Every failure says what happened, why, and what to do, with technical details in a report that can be copied.

The interface is in English, Portuguese, French and Spanish, and follows Reduce Motion and the system's light or dark taskbar.

## Privacy

Split doesn't use the Internet. Everything runs on your PC: conversion, OCR (Windows' own), face detection, QR reading and background removal (a model that is installed with the program). The program contains no network code, and the tests check that rather than just promise it.

- ffmpeg may only open local files, so a playlist disguised as a video can't send it online.
- HTML, Markdown, EPUB and SVG are read without loading anything remote; XML is read without external entities and with an expansion limit.
- Archives can't write outside their destination folder, and links that point outside it are dropped.
- File names never go through a shell.

**An exception you choose:** with "Use Word, Excel and PowerPoint when installed" on, those conversions are done by Microsoft's apps, which follow their own rules. Turn it off to use only Split's own converters.

## Optional engines

Settings › Engines shows the exact command for whatever is missing, and `splitapp doctor` lists them. There's no need to restart Split afterwards.

```powershell
winget install -e --id Gyan.FFmpeg                        # audio, video, GIF, subtitles
winget install -e --id OliverBetz.ExifTool                # removing image metadata
winget install -e --id TheDocumentFoundation.LibreOffice  # an alternative for ODS, ODP, PPT, DOC
```

## Command line

Choose **Add splitapp to PATH** in the installer (or add the install folder to your PATH), then open a new terminal:

```powershell
splitapp convert webp $HOME\Desktop\*.heic
splitapp targets report.docx
splitapp tool compress video.mov
splitapp doctor
splitapp help
```

Without a command, `splitapp` starts the app.

## Build from source

You need the .NET 10 SDK; Inno Setup 6 only for the installer.

```powershell
./tools/get-assets.ps1    # the fonts and icons (already in git; checks their SHA-256)
./tools/get-model.ps1     # once: the background-removal model (about 170 MB, git-ignored)
./build.ps1               # builds everything (Release)
./test.ps1                # builds and runs every test; ./test.ps1 <filter> runs some
./make-installer.ps1      # build/installer/Split-<version>-<arch>-Setup.exe and .sha256, x64 and arm64
```

The version is `<Version>` in `Directory.Build.props`; the About window shows it. `tools/MakeIcon` redraws `src/Split.App/Split.ico` from the logo code. The first restore needs the network once; after that builds are offline. Tests that need Word, Excel and PowerPoint run with `$env:SPLIT_OFFICE=1`.

The code is in `src/Split.Core` (conversions, tools, editor models, wheel layout; no WPF), `src/Split.Windows` (Windows integration), `src/Split.App` (the program) and `src/Split.Cli` (`splitapp`).

## Licenses

Split bundles these components:

| Component | Licence |
|---|---|
| [Inter](https://rsms.me/inter/) (font) | SIL OFL 1.1 (`assets/fonts/OFL.txt`) |
| [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) | MIT (`assets/fonts/LICENSE-FluentSystemIcons.txt`) |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) (ImageMagick) | Apache-2.0 |
| [PDFsharp and MigraDoc](https://github.com/empira/PDFsharp) 6.2.4 | MIT (later versions are not, so this one is pinned) |
| [PDFium](https://pdfium.googlesource.com/pdfium/) binaries by bblanchon | Apache-2.0 / BSD-3-Clause |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) | MIT |
| IS-Net `isnet-general-use` model | Apache-2.0 |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | MIT |
| [ZXing.Net](https://github.com/micjahn/ZXing.Net) | Apache-2.0 |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | MIT |
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | MIT |
| [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia) | MIT |
| [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) | MIT |
| .NET runtime and WPF | MIT |

ffmpeg, ExifTool and LibreOffice are not bundled: they are separate programs you install yourself, under their own licences.
