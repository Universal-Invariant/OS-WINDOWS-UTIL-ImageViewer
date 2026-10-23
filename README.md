### ImageViewer

A very simple ImageViewer that can display images recursively and scale them. The main use is to be able to display a lot of small images scattered in subdirectories quickly. It was a very old program that I never got around to finishing but may still be useful.


<img width="1146" height="991" alt="{1E91C43E-0A1A-4031-A2A1-4AD777B8CA1F}" src="https://github.com/user-attachments/assets/be7c96b2-132e-493f-a660-5c851e4802bc" />

## Features

- **Folder browsing** — type a path or click the folder icon to pick a directory visually.
- **Recursive scan toggle** — one compact two-state toolbar button to include/exclude subdirectories.
- **Scale slider** — live re-scaling of every image (0.1x – 3.0x) with a flow layout that keeps images from overlapping at any scale.
- **Fit-to-width "book format" toggle** — scales all images to the window width, ideal for scanning pages or comics of similar size.
- **Modern titlebarless UI** — borderless window with drag region, custom minimize/maximize/close buttons; compact icon-only toolbar (folder, gear, recurse, fit-width).
- **Settings dialog** — gear button opens a simple dialog for basic options (default scale, max image size, recursion default, theme accents).
- **Lazy loading / virtualization** — only images near the viewport are decoded, so huge directories stay responsive.
- **Status bar** — hover an image to see its full path; item count and load progress in the status strip.

## Recommended future features

- **Lightbox / double-click zoom** — open a single image full-screen with keyboard navigation (←/→, Esc).
- **Keyboard shortcuts** — `Ctrl+O` browse, `+/-` scale, `W` toggle fit-to-width, `R` toggle recursion, `F5` refresh.
- **Sorting options** — by name, date taken, size; ascending/descending.
- **Filter box** — glob/substring filter over file names (e.g. `*.jpg`, `vacation`).
- **Per-folder settings persistence** — remember scale + fit-width per path.
- **EXIF metadata panel** — show dimensions, camera, date next to the hovered image.
- **Slideshow mode** — auto-scroll at configurable interval, handy for reviewing scans.
- **Dark/light theme toggle** — move accent/theme choice into the settings dialog.
- **Image formats** — add WebP/HEIF/AVIF via WinCodec, and thumbnail caching to disk for faster reloads.
- **Multi-select & actions** — select several images and copy/delete/open-in-explorer.

## Building

Requires Visual Studio 2019+ (or MSBuild) with the .NET Framework desktop workload. Open `ImageViewer.sln` and build the `ImageViewer` project (WPF, x86/x64 Any CPU).
