### ImageViewer

A fast Windows image viewer for browsing many images at once: icon collections scattered over subfolders, photo folders, scanned pages and comics. It shows a whole folder (optionally with all its subfolders) as one scrollable flow of images, decodes only what is near the screen, and stays at 60 fps with thousands of images.

<img width="3428" height="1377" alt="Screenshot 2026-10-31 150713" src="https://github.com/user-attachments/assets/0c8b3de8-9bc9-48cd-90e4-5dfe71e4d53d" />

Press **F1** (or click **?** in the toolbar) in the app for the complete list of keyboard shortcuts.

## Contents

- [Opening folders](#opening-folders)
- [Browsing the grid](#browsing-the-grid)
- [Layouts: scale, tiles, packing, book view](#layouts-scale-tiles-packing-book-view)
- [Preview, zoom and slideshow](#preview-zoom-and-slideshow)
- [Working with images](#working-with-images)
- [Info panel](#info-panel)
- [Window, full screen and profiles](#window-full-screen-and-profiles)
- [Settings](#settings)
- [Keyboard and mouse reference](#keyboard-and-mouse-reference)
- [Formats and performance](#formats-and-performance)
- [Command line and benchmark](#command-line-and-benchmark)
- [Where data is stored](#where-data-is-stored)
- [Building](#building)
- [Recommended future features](#recommended-future-features)

## Opening folders

- **Path box with breadcrumb** — click any folder segment of the path to jump there; click empty space in the box (or Ctrl+L) to type a path and press Enter. The dropdown lists recently used folders. A path that doesn't exist turns red.
- **Folder picker** — the folder button or Ctrl+O. Ctrl+click the folder button to **add** a folder to the ones shown.
- **Drag and drop** — drop a folder onto the window to open it. Drop an image to open its folder and preview that image. Drop several folders (or images from several folders) to show them together; hold **Ctrl** while dropping to add them to the folders already shown.
- **Several folders as one view** — type them into the path box separated by `;` (`D:\Comics\Vol 1; E:\Scans`), drop several, Ctrl+drop or Ctrl+browse to add, or select several folders or images in Explorer and use *Open in Image Viewer*. Each folder gets its own header (in the order given), and sorting, filtering (a folder's name filters too), packing, book view and the slideshow work across all of them. The path bar shows `Vol 1 + Scans`; click a name to show that folder alone. The set is remembered like a single folder: recent folders, history, reading position and profiles. A folder inside another one shown with subfolders is not listed twice.
- **Command line / Explorer** — `ImageViewer.exe <folder or image> [more folders or images…]`. Settings → *Add "Open in Image Viewer" to Explorer's right-click menu* adds the entry for folders, folder backgrounds and image files (per user, no admin rights). With several items selected in Explorer they all open in one window (Explorer starts one viewer per item; the first one collects the others for a moment, and those close again). Explorer shows the entry for up to 15 selected items.
- **Include subfolders** — the toolbar toggle shows every image below the folder. Each subfolder gets a header line with its image count; click a header to open that folder. Inaccessible subfolders are skipped.
- **History** — Back / Forward (toolbar arrows, Alt+← / Alt+→, mouse back/forward buttons), parent folder (Alt+↑), previous / next sibling folder (Ctrl+PageUp / Ctrl+PageDown — e.g. the next chapter of a comic). Parent and sibling need a single folder.
- **Folder changes** — when images are added, removed or renamed on disk by another program, a red refresh button appears (F5 reloads). Settings can make the viewer reload by itself after a short quiet period. Changes the viewer makes itself (rename, move, delete) are applied directly.
- **Remembered position** — the viewer remembers where you were in each folder and returns there when you open it again (Settings → *Remember where I was in each folder*).

## Browsing the grid

- **Mouse** — the image under the mouse is highlighted; the status bar shows its name, pixel size, file size, date and folder.
- **Keyboard focus** — the arrow keys move a dashed focus outline from image to image (up/down pick the image closest above/below). Home / End jump to the first / last image. Shift+arrows extend the selection, Ctrl+arrows move the focus without selecting, Insert toggles the focused image. Once you use the arrow keys, keys like Space, Enter, R, F2 and Del act on the focused image; moving the mouse hands control back to the image under the mouse.
- **Selection** — click, Ctrl+click (toggle), Shift+click (range), Ctrl+A (all), click empty space or Esc to clear. Actions on an image that is part of the selection apply to the whole selection.
- **Filter** — Ctrl+F or the filter box. Words match file names and subfolder paths (every word must match); wildcards such as `*.png` or `img_0??` match file names (or relative paths when they contain `\`). Down arrow moves from the filter box to the images; Esc clears.
- **Sorting** — View button (sort icon): name (natural order — `img2` before `img10`), date modified, file size or dimensions; ascending or descending; *Group by folder* keeps subfolders together under their headers.
- **Scrolling** — wheel, scrollbar (appears at the right edge), PageUp/PageDown. **Row scrolling** (toolbar toggle) makes each wheel notch or PageUp/PageDown move whole rows; images taller than the window are paged with a configurable overlap.
- **Status bar** — position ("12 / 240 images"), filter count, selection count, load progress, hovered image details, active modes and the current profile.

## Layouts: scale, tiles, packing, book view

- **Scale** — slider, −/+ buttons, Ctrl+wheel or Ctrl +/−/0 (10% – 400%). Click the percentage to reset to 100%.
- **Largest image** — very large images are first limited to a share of the window (Settings → *Largest image*), so a 6000 px photo doesn't fill ten screens. The limit follows the window size live.
- **Uniform tiles** (T) — every image in an equal square tile, regardless of its size.
- **File names** (N) — names under the images.
- **Packed grid** (C, the arrange button next to the book-view toggle) — normally images flow in rows and the tallest image of a row sets its height, leaving empty space around the smaller ones. Packed, each row is filled like a puzzle: smaller images stack in columns beside and under the large ones, taking images from a little further along the folder (up to about 50 places ahead) to fill the gaps, so more images fit on screen (a folder of mixed sizes needed about a quarter less scrolling and half the empty space in testing). The order changes only locally; arrow keys move to the image visually beside, above or below, Shift-selection follows the order on screen, and the preview still steps through the folder in its sort order. Folder groups are packed separately.
- **Backdrop for transparent images** — icons and other transparent images are drawn on a backdrop so dark or light artwork stays visible. One click on the **swatch button** in the toolbar (or **B**) steps through checkerboard → *same as background* → black → white → dark red → dark green → dark blue → checkerboard; Shift+click (Shift+B) goes back; right-click the swatch to pick a color (or *Nothing*) directly. The swatch shows the current backdrop, a short message names it, and it applies in the grid and in the preview. *Same as background* uses the color of the image area itself (Settings → *Image background*) and follows it when that changes, so transparent images blend seamlessly into the background; when the background already is one of the listed colors (black by default), that entry is marked *(same as background)* instead of appearing twice. Settings → Appearance additionally offers light and dark gray.
- **Book view** (F, toolbar toggle) — pages (images at least *Book view: minimum image size* wide or tall) are scaled to the window width and centered; smaller images keep their scale and flow in between. Space / Shift+Space turn pages. In book view the arrange button (C) switches centering off and on (pages aligned to the left / right-to-left edge instead). In the View menu:
  - *Two pages side by side* — spreads, like an open book.
  - *Cover page on its own* — the first page of each folder gets its own row so pages 2+3, 4+5… face each other as printed.
  - *Right to left* — pages and rows run from the right (manga).

## Preview, zoom and slideshow

- **Open** — Space, the context menu, or double-click (when Settings → *Double-click opens images in the default app* is off). Enter opens the image in its default app instead.
- **Step** — ← / → (also ↑ ↓, PageUp/PageDown), the wheel, the arrow buttons, Home / End. The next image is pre-loaded.
- **Zoom** — small images are shown at actual size, large ones fit the window. Ctrl+wheel zooms at the mouse; + / − zoom at the center; **Z** or double-click toggles fit ↔ 100% (actual pixels; images that already fit go to 200%); 0 returns to fit. While zoomed in, drag or use the wheel to pan (Shift+wheel sideways). The full-resolution image is decoded when you zoom past the screen-size copy. The caption shows the zoom.
- **Animated GIFs** play in the preview.
- **In the preview** — R / Shift+R rotate, F2 renames, Del deletes, I toggles the info panel, B / Shift+B change the backdrop of transparent images, Ctrl+C copies, Esc leaves the zoom, then closes. Closing lands the grid on the image you ended on.
- **Slideshow** — S (or *Slideshow from here* in the context menu, or the play button, or Ctrl+Space in the preview) starts from the current image and wraps around at the end. The interval is set in Settings. S pauses, Esc stops.
- **Auto-advance (hands-free reading)** — Ctrl+Space in the grid: every few seconds the grid scrolls one screen (starting with the first row not yet fully seen), and book view turns one page, tall pages a screen at a time. Scrolling or pressing a key yourself gives you a full interval from there; it pauses while the preview or help is open, and stops at the end, when another folder opens, or with Esc / Ctrl+Space. The status bar shows it while it runs. The interval is Settings → Browsing → *Auto-advance interval* (1 – 60 s, default 8 s).

## Working with images

All of these act on the selection when the target image is part of it.

- **Rotate** — R / Shift+R, Alt+click / Alt+Shift+click, or the context menu (90° right, 180°, 90° left, reset). Rotations are remembered per file and never modify the image. EXIF orientation of photos is applied automatically.
- **Rename** — F2 (grid or preview). The name is pre-selected without the extension; the image keeps its rotation and moves to its new sort position.
- **Copy** — Ctrl+C copies the files (paste into Explorer, mail…) and, for a single image, also the picture (paste into editors). Ctrl+Shift+C copies the paths.
- **Copy / move to folder…** — context menu; name clashes get " (2)" etc.
- **Delete** — Del moves to the Recycle Bin (asks first).
- **Drag out** — drag images to Explorer, an editor or any app that accepts files.
- **Show in Explorer**, **Open in default app** — context menu.
- Short confirmations ("Image copied", "Renamed to …") appear at the top of the image area.

## Info panel

**I** (or View menu → *Info panel*) opens a panel on the right with details of the image under the mouse, the keyboard focus, or the image in the preview:

- name, folder, dimensions and megapixels, type, file size, modification date, viewer rotation, position in the folder;
- camera data from EXIF (JPEG, TIFF): camera, lens, date taken, exposure, aperture, ISO, focal length (and 35 mm equivalent), flash, GPS location, software.

Values can be selected and copied. The panel's state is remembered.

## Window, full screen and profiles

- **Title bar** — compact toolbar with native window behavior: drag empty space to move, double-click to maximize, resize from any edge, Snap layouts.
- **Full screen** — F11 (or View menu): only the images, covering the whole monitor including the taskbar. Move the mouse to the top edge to use the toolbar; F11 or Esc leaves.
- **Themes** — Dark, Light, or System (follows the Windows app mode).
- **Profiles per monitor / virtual desktop** — optional (Settings → Profiles): every setting, including the folder, window size/position/state, scale and view options, is remembered separately for each monitor and/or Windows virtual desktop. The profile of the monitor/desktop the viewer starts on is used: the monitor of the taskbar, Start menu or Explorer window it was launched from (Windows passes it along), else the monitor of the window in front (a terminal, an IDE), else the mouse position. A new place starts as a copy of the last one used. Settings → Profiles lists every saved profile with its monitor and desktop, folder, window state, size and position and when it was last used, marks the one for the window's current place, and shows which profile the settings came from at start; profiles can be forgotten there.

## Settings

The gear button opens Settings:

| Section | Options |
|---|---|
| Browsing | Include subfolders · Reload automatically when files are added or removed · Double-click opens images in the default app (otherwise: preview) · Remember where I was in each folder · Explorer right-click menu entry · Slideshow interval · Auto-advance interval |
| Layout | Book view on/off · Book view: minimum image size (smaller images keep their scale) · Largest image (% of window) · Default scale |
| Row scrolling | Rows per wheel notch · Overlap when paging tall images |
| Profiles | Separate settings for each monitor · for each virtual desktop · Saved profiles with folder, window state, size and position (forget) |
| Appearance | Show the status bar · Theme (Dark / Light / System) · Behind transparent images (nothing / checkers / light / dark; the toolbar swatch adds same-as-background, black, white, dark red, dark green, dark blue) · Image background color |

View-menu options (sort, grouping, tiles, names, two-page spread, cover page, right to left, info panel) and toolbar toggles are remembered as well.

## Keyboard and mouse reference

| Keys | Action |
|---|---|
| **Folders** | |
| Ctrl+O / Ctrl+L | Open folder / type a path |
| Alt+← / Alt+→ / Alt+↑ | Back / forward / parent folder (mouse back/forward buttons) |
| Ctrl+PageUp / Ctrl+PageDown | Previous / next sibling folder |
| F5 | Reload |
| **Browsing** | |
| Arrow keys, Home / End | Move the keyboard focus (and select) / first, last image |
| Shift+arrows / Ctrl+arrows / Insert | Extend the selection / move the focus only / toggle the focused image |
| Click / Ctrl+click / Shift+click, Ctrl+A | Select / toggle / range, select all |
| PageUp / PageDown | Scroll a screen (row scrolling mode: rows) |
| Ctrl+F | Filter |
| Esc | Clear the filter, then the selection, then leave full screen |
| **View** | |
| Ctrl + / Ctrl − / Ctrl 0, Ctrl+wheel | Scale up / down / 100% |
| F | Book view (fit pages to the window width) |
| C | Packed grid / in book view: center the pages (arrange button) |
| Space / Shift+Space | Book view: next / previous page |
| T / N | Uniform tiles / file names |
| B / Shift+B | Next / previous backdrop color for transparent images (toolbar swatch; right-click it to choose) |
| I | Info panel |
| F11 | Full screen |
| F1 or ? | Keyboard shortcuts and help |
| **Images** | |
| Space | Preview (book view: next page) |
| Enter, double-click | Open in the default app (double-click: preview if set in Settings) |
| R / Shift+R, Alt+click / Alt+Shift+click | Rotate right / left |
| F2 | Rename |
| Del | Move to the Recycle Bin |
| Ctrl+C / Ctrl+Shift+C | Copy image(s) / path(s) |
| S | Slideshow |
| Ctrl+Space | Auto-advance: next screen / page every few seconds (Esc stops); in the preview: slideshow |
| Drag | Drag images out to other apps |
| **Preview** | |
| ← → ↑ ↓, PageUp/PageDown, wheel | Previous / next image |
| Home / End | First / last image |
| Ctrl+wheel, + / −, 0 | Zoom at the mouse / at the center / fit |
| Z, double-click | Fit ↔ 100% |
| Drag, wheel, Shift+wheel | Pan while zoomed in |
| R, S, I, F2, Del, Ctrl+C | Rotate, slideshow, info panel, rename, delete, copy |
| B / Shift+B | Backdrop color of transparent images |
| Esc / Space | Leave zoom, then close / close |

## Formats and performance

- **Formats** — JPEG, PNG, BMP, GIF (animated in the preview), ICO, TIFF, plus WebP, HEIC/HEIF, AVIF and JPEG XR when the Windows codecs for them are installed. Unreadable files are skipped.
- **UI virtualization** — only rows near the viewport exist as UI elements; positions are computed arithmetically, so relayouts of thousands of images take milliseconds.
- **Lazy decoding** — only images near the viewport are decoded, on background threads and at their displayed size (sharper copies are fetched when you enlarge them). Recently viewed bitmaps stay in a 256 MB cache, so scrolling back is instant.
- **Measured** (Release, 125% DPI, built-in benchmark): 5,000 icons load in 0.17 s and scroll at 60 fps; scale, book-view and window-size relayouts take 5–20 ms; 300 large photos scroll at 60 fps with no blank images.

## Command line and benchmark

```
ImageViewer.exe [<folder or image> ...]
ImageViewer.exe --benchmark <folder> [--out report.txt] [--cache-mb N] [--software]
```

`--benchmark` loads the folder (without subfolders), scrolls through it frame by frame, changes scale / book view / window size and writes a timing report: frame times, blank images while scrolling, per-stage timings and memory. It runs on default settings and never saves anything. The report goes to `%TEMP%\ImageViewer-benchmark.txt` unless `--out` is given. `--cache-mb` changes the bitmap cache size, `--software` forces software rendering.

## Where data is stored

- Settings: the standard .NET user settings (`%LOCALAPPDATA%\Microsoft\ImageViewer.exe_Url_…\user.config`).
- `%LOCALAPPDATA%\ImageViewer\profiles.xml` — per-monitor / per-desktop profiles.
- `%LOCALAPPDATA%\ImageViewer\rotations.xml` — rotations you applied (images are never modified).
- `%LOCALAPPDATA%\ImageViewer\positions.xml` — the remembered position in each folder.
- Explorer menu entry (only when enabled): `HKCU\Software\Classes\…\shell\ImageViewer`.

## Building

Requires the .NET Framework 4.8.1 developer pack and either Visual Studio 2019+ (".NET desktop development" workload) or the .NET SDK. Open `ImageViewer.sln` and build the `ImageViewer` project, or from the command line:

```
dotnet build ImageViewer/ImageViewer.csproj -c Release
```

Note: the project's pre-build step closes a running `ImageViewer.exe`, and its post-build step copies the built exe to `C:\Apps\Command`.

## Recommended future features

- **Thumbnail caching to disk** — faster reloads of large or network folders.
- **Sort by date taken** (EXIF) and show it in the status bar.
- **Batch rename** of the selection with a pattern (`page_###`).
- **Compare mode** — two images side by side in the preview with linked zoom.
- **Editing basics** — crop and save-as, lossless JPEG rotation written to the file.
