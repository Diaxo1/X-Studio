# X Studio bundled components

This alpha package includes the following separately licensed programs. Their notices and license files are shipped alongside the binaries under `tools`.

- **UxPlay for Windows 2.0.0.1736** — GPL-3.0 for the app and UxPlay; GStreamer components are LGPL; mDNSResponder components include BSD-3-Clause and Apache-2.0. See `tools/uxplay/LICENSE.rtf`. Sources: [UxPlay for Windows](https://github.com/leapbtw/uxplay-windows/tree/2.0.0.1736), [UxPlay](https://github.com/FDH2/UxPlay), [mDNSResponder](https://github.com/apple-oss-distributions/mDNSResponder), and [GStreamer](https://gstreamer.freedesktop.org/).
- **scrcpy 5.0.1** — Apache-2.0. See `tools/scrcpy/LICENSE.txt`. Source: [Genymobile/scrcpy](https://github.com/Genymobile/scrcpy).
- **MediaMTX 1.21.3** — MIT. See `tools/mediamtx/LICENSE`. Source: [bluenviron/mediamtx](https://github.com/bluenviron/mediamtx).
- **FFmpeg 9.0.2 (Gyan build)** — GPL-3.0 build with libx264 enabled. FFmpeg is a separate executable process. See [FFmpeg legal information](https://ffmpeg.org/legal.html) and [Gyan builds](https://www.gyan.dev/ffmpeg/builds/).
- **.NET 10 Windows Desktop runtime** — bundled self-contained runtime. See the Microsoft notices included in the publish output.

Apple Bonjour is **not redistributed** in this package because Apple's license does not permit external redistribution. If it is missing, X Studio downloads the Apple MSI directly from Apple's server, verifies its SHA-256 before use, and asks Windows for administrator approval to install the service.

This package is for alpha evaluation. Before public redistribution, provide the corresponding source code and any other materials required by the licenses of the bundled GPL/LGPL components.
