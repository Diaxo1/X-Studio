# X Studio

X Studio is a lightweight Windows streaming-app prototype. Its current native app provides a compact live desk, a dedicated destination setup page, encrypted local storage for stream keys, and an FFmpeg-based RTMP engine.

## Run from source

Install the .NET 10 SDK and the runtime tools with `package-release.ps1`, or run from a machine with the existing FFmpeg, scrcpy, and MediaMTX WinGet packages:

```powershell
dotnet run --project desktop\desktop.csproj
```

If `dotnet` is not on `PATH`, use `"C:\Program Files\dotnet\dotnet.exe" run --project desktop\desktop.csproj`.

## Build the Windows alpha installer kit

Run `powershell -ExecutionPolicy Bypass -File .\package-release.ps1` to publish the self-contained Windows app, then run `powershell -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -PackageZip .\dist\<package-name>.zip`. This creates a setup kit ZIP. Extract it and double-click `Install-XStudio.cmd` to choose an install folder, unpack the app and bundled dependencies, add a Start menu shortcut, and register an Apps & Features uninstaller. Setup is per-user and needs no administrator access or .NET runtime on the target PC. The app includes FFmpeg, MediaMTX, scrcpy/ADB, and the UxPlay runtime including mDNSResponder. Android phone casting needs USB debugging. iPhone casting uses the bundled UxPlay receiver and mDNS runtime.

The GitHub Actions workflow builds this setup kit on pushes to `main` or `master`, and can also be run manually from the Actions tab. Download the `XStudio-Windows-Alpha-Installer` artifact from a successful run.

## Current streaming path

- Captures the Windows desktop or selected app windows as independent scene layers using FFmpeg `gdigrab`; app windows resolve through their live window handles.
- Captures one selected microphone through DirectShow, or disables audio.
- Adds a live microphone test meter and checks that the selected input opens before a broadcast. Broadcast audio is mapped to AAC for every selected destination.
- Offers 720p30 and 480p30 output profiles.
- Attempts Media Foundation H.264 encoding, then falls back to x264 ultrafast when hardware initialization fails.
- Runs one FFmpeg broadcast session with shared capture and microphone inputs. It composes synchronized wide (16:9) and portrait (9:16) outputs, routes TikTok destinations to portrait, and tees each encode to its matching RTMP/RTMPS destinations.
- Can start a local, loopback-only MediaMTX RTMP receiver and add its URL as a selected test destination.
- Adds Android phone screens as scene layers through bundled scrcpy. Connect over USB, enable Developer options → USB debugging, approve the phone's trust prompt, then use Settings → Phone Cast.
- Adds an iPhone AirPlay receiver through bundled UxPlay for Windows. Keep the phone and PC on the same Wi-Fi, choose Screen Mirroring on iPhone, and select this PC. Add an iPhone Phone source to the scene; X Studio follows the receiver's live window into the scene preview. Protected video apps may block mirroring.
- Keeps the live preview running while a phone is waiting to connect or a camera is unavailable; those sources show an on-canvas placeholder instead of stopping desktop capture. Phone windows are attached as soon as they appear.
- The Live Desk keeps the active scene, display/game-window capture target, and camera selector in the top setup bar. Scene source layers can be dragged, resized, removed, and double-clicked in the preview. The preview can switch between 16:9 and 9:16 without changing the active scene. Destination platform determines the broadcast aspect: TikTok receives portrait while other platforms receive wide, within the same synchronized broadcast session.
- Lets you drag scene cards to reorder the library and drag source layers in the Scene Library to set their front/back depth.
- Scene layers can include a clean empty canvas, a picture or looping video background, full-display capture, selected app windows, camera, phone cast, images, and text. Each layer has independent position, width, height, and crop controls.
- The live preview's crop mode lets you drag a source edge to trim it. Scene creation separates Wide and TikTok template choices and shows a miniature layout preview; custom templates preserve the source arrangement.
- Includes reusable templates for gameplay, just chatting, vertical phone, screen tutorial, and vertical showcase. Save any active scene as a custom template; custom templates stay on this PC under `%LOCALAPPDATA%\XStudio\templates.json`.
- Saves destination details under `%LOCALAPPDATA%\XStudio`; stream keys are protected with Windows DPAPI for the current Windows account.

The Live desk's destination selector determines which configured outputs are included in the next broadcast. Add each platform's RTMP ingest URL and stream key on the Destinations page. X Studio does not authenticate with TikTok, YouTube, or Twitch; you must provide an ingest URL and key supported by your account. Do not share your stream key. FFmpeg receives the constructed output URL as a process argument while broadcasting, which may be visible to local process-inspection tools.

## Scope and limits

This is an early Windows proof of concept. The preview composes display/app, camera, phone, image/video, background, and text inputs through FFmpeg at reduced resolution and frame rate to limit overhead. When both wide and TikTok destinations are selected, FFmpeg encodes two output variants from the same live inputs; software fallback can use more CPU than a single-format stream. A physical phone and microphone broadcast were not available for a device smoke check. Desktop/system audio mixing and the third-party plugin API are not yet implemented. Check the stream output, encoder state, and machine load before relying on it for a live broadcast.

The setup kit contains third-party GPL/LGPL programs (FFmpeg and UxPlay). See `THIRD-PARTY-NOTICES.md`; corresponding source code and notices must be provided before public redistribution. The alpha setup kit is currently unsigned, so Windows may show an unknown-publisher prompt.

The source includes an earlier browser concept and Node engine spike; the native WPF app in `desktop` is the active implementation.

