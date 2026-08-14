# Changelog

## Unreleased

- Changed activation back to build-specific semantics: Editor Play Mode stays enabled, while `Enable In Build` consistently gates automatic and API-driven Player activation.
- Added an IMGUI software cursor with resolution-consistent apparent size and movement for hidden or locked desktop pointers without changing Unity cursor state, using an optional Input System raw-delta adapter, automatic mobile/handheld/console exclusions, and a Steamworks-independent runtime opt-out for PC handhelds.
- Added a host-provided Handheld Mode that reuses the touch-oriented Entry Button without its keyboard label and excludes the desktop software cursor, while keeping Steamworks outside the package.
- Added IMGUI Screenshot and Video review tabs with API-only `VideoPlayer` playback, click/tap play-pause, and a draggable in-frame timeline for finalized H.264 MP4 incident files.
- Added per-report Screenshot and Video inclusion toggles together in the right-side Attachments section, while keeping the left review tabs focused on media preview.
- Placed the two media choices on one row, using selected color instead of ON/OFF text, and removed selection-like hover feedback from non-interactive report text.
- Fixed software-cursor dragging for the left, right, and compact report-page scrollbars.
- Fixed screenshot review clipping and unwanted horizontal scrolling by wrapping annotation controls from the capture column's actual width.
- Removed the redundant Play and Restart row after moving playback and seeking into the video frame.
- Kept valid recordings attachable when local preview is unavailable, including AVI fallback and decoder failures.
- Fixed Linux and native Steam Deck incident finalization by converting generic disk-backed RGBA frames for the existing managed MJPEG AVI fallback.
- Fixed Windows GPU rolling-video recovery by rejecting native sessions with initialization errors and switching create, submit, segment-finalize, or merge failures to the existing generic MP4/AVI recorder without adding Steamworks or device detection.
- Added opt-in Windows video backend diagnostics for Steam Launch Options, with isolated GPU, CPU Media Foundation, and managed AVI paths plus operation-specific native HRESULT errors; the default Windows GPU path and capture settings are unchanged.
- Preserved a bounded video backend/fallback timeline in diagnostics independently from recent gameplay logs, including screen/output dimensions, frame count, duration, and effective FPS.
- Enabled non-WebGL report pages to attempt managed MJPEG AVI preview through the existing Unity `VideoPlayer`, while keeping valid files attachable when the platform decoder rejects them.
- Documented that `Video Width` intentionally scales output resolution while preserving aspect ratio instead of changing native screen resolution or capture performance policy.
- Fixed generic fallback capture on Proton/D3D by explicitly scaling the complete backbuffer before readback, without changing preferred macOS or Windows GPU recording.
- Added a bounded managed fallback for MacacaBeacon-authored MJPEG AVI when `VideoPlayer` errors or returns implausible duration, frame, or aspect metadata; working native Windows AVI, MP4, and other AVI encoders keep the existing `VideoPlayer` path.

## 0.5.1

- Added compile-time Production isolation with the `MACACA_BEACON_PRODUCTION` define.
- Also accepts the conventional `PRODUCTION` define as an equivalent switch.
- Production builds retain a disabled `BugReporterSettings` shell without compiling project-specific configuration fields.
- Disabled automatic startup, manual opening, and rolling-video toggling in Production builds.

## 0.5.0

- Replaced the normal rolling JPEG capture path with asynchronous RGBA GPU readback cached to temporary files.
- Added raw RGBA inputs to the Android MediaCodec, Apple AVAssetWriter, and Windows Media Foundation H.264 backends.
- Restored AVAssetWriter's automatic hardware/software encoder selection and enabled real-time input pacing on Apple platforms.
- Corrected vertical orientation for raw RGBA video encoded by AVAssetWriter on macOS and iOS.
- Moved Android raw-frame MediaCodec finalization to a Java worker job so report form input remains responsive while video encoding runs.
- Increased the default raw cache to 512 MB and added requested-versus-available history logging for portrait captures.
- Kept JPEG/MJPEG only as a compatibility fallback when asynchronous GPU readback is unavailable.

## [0.4.0] - 2026-08-08

- Added an iOS AVAssetWriter H.264 MP4 backend compiled directly into Unity's generated Xcode project through `__Internal`.
- Added iOS framework dependencies and hardware H.264 settings for device builds.
- Added low-interference mobile entry points: a safe-area corner button and configurable three-finger hold gesture.

## [0.3.0] - 2026-08-07

- Added a Windows x64 Media Foundation backend that encodes rolling JPEG frames as H.264 MP4 in the Windows Editor and Standalone Player.
- Added native WIC JPEG decoding, real presentation timestamps, and an incident-boundary hold frame to preserve requested video duration.
- Added a Visual Studio 2022 rebuild script and Windows-only Unity plugin import settings; the Player does not require ffmpeg or a third-party codec runtime.

## [0.2.0] - 2026-08-07

- Added a universal macOS native backend that finalizes rolling JPEG frames as H.264 MP4 using AVAssetWriter in Editor and Player builds.
- Added MP4-first encoder selection with an optional managed MJPEG AVI fallback on unsupported or temporarily unavailable encoders.
- Moved video finalization off the Unity main thread and preserved real capture timestamps and the requested incident duration.
- Added file-backed report attachments and `UploadHandlerFile` streaming so videos no longer require a second full in-memory copy.
- Local report staging now copies the completed video before Slack upload and retains it when delivery fails.
- Added MP4 bitrate, MP4 preference, and legacy fallback settings.

## [0.1.0] - 2026-08-07

- Initial Macaca Beacon embedded UPM package.
- Added F6 IMGUI reporter, screenshots, diagnostics, recent logs, Slack Bot message/file delivery, threaded attachments, and optional rolling MJPEG video.
- Added a transactional local outbox that retains failed reports and attachments for manual upload.
- Added screenshot brush annotations with color/size controls, undo, reversible clear, and PNG export.
