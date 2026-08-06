# Changelog

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
