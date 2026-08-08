# Macaca Beacon

> Capture the moment. Signal the issue.

遊戲內、可抽離為 UPM 的 Bug Report 工具。它使用 **IMGUI**（不依賴 UGUI），桌面可用 F6 開啟，iOS／Android 則提供安全區角落的低干擾入口與三指長按手勢；工具會收集截圖、最近 log、裝置／build／場景資訊，並送到 Slack。

截圖預覽提供單層內建標注畫筆，圖片顯示後即可直接繪製，不需要進入另一個模式。工具列固定提供紅／黃／青三色、三種筆刷粗細、Undo、可復原的 Clear，以及重新截圖。完成的筆跡會合成進 Slack 與本地失敗備援所使用的 PNG。

## 專案內啟用

這個 repository 已將套件放在 `Packages/com.macacagames.beacon`，Unity 會把它視為 embedded package。桌面進入 Play Mode 後直接按 **F6** 即可開啟；不需要在 Scene 放 prefab。手機平台預設顯示一個只佔自身矩形的小型 `!` 入口，也可以用三指按住約 0.75 秒開啟。

第一次設定請開啟：

`Tools > Macaca Beacon > Open Settings`

設定資產會建立於 `Assets/Resources/BugReporterSettings.asset`。

`Appearance` 預設使用全螢幕並將介面縮放設為 1.25，寬螢幕採左右雙欄，小尺寸 Game View 自動切換成可捲動單欄。關閉 `Fullscreen` 後會改用置中視窗，並可調整背景遮罩透明度與桌面視窗寬度比例。

手機入口設定位於 `Mobile entry`：可分別關閉角落按鈕或三指手勢，調整按鈕尺寸／透明度／位置。按鈕會依 `Screen.safeArea` 避開瀏海與 Home indicator，只有觸控落在按鈕自己的矩形內時才會攔截事件，不會替整個遊戲畫面鎖定觸控。

## Slack 設定

1. 建立 Slack App，替 Bot 加入 `chat:write` 與 `files:write` scopes，然後安裝／重新安裝 App。
2. 將 `Bot Token` 與目標 `Channel ID` 填入 Macaca Beacon Settings。
3. 邀請該 App 加入目標 channel。

主回報固定由 Bot 使用 `chat.postMessage` 發送並取得父訊息 `ts`；附件再依 Slack 官方流程走 `files.getUploadURLExternal` → 檔案位元組上傳 → 帶有 `thread_ts` 的 `files.completeUploadExternal`，因此截圖、影片與 diagnostics 都位於主回報的 Thread。套件不使用 Incoming Webhook。

> 安全性：Bot Token 放進 Player 後可被擷取。僅建議內部／受信任測試使用。公開玩家版本應實作自己的 rate-limited relay，並以 `BugReporter.SetTransport(...)` 替換內建 transport；不要把 Slack secret 發佈給玩家。

## 會送出的資料

- 使用者輸入：分類、標題、描述、選填聯絡資訊
- PNG 截圖（按 F6、手機入口或三指手勢後、面板出現前擷取）
- 截圖上的選填畫筆標注
- Product、version、build GUID、Unity、platform、OS、CPU、RAM、GPU、VRAM、resolution、scene
- 有固定容量上限的 recent log ring buffer；Error／Exception 包含 stack trace
- 選填影片：macOS／Windows／iOS 優先 H.264 MP4，預設 6 FPS、前 8 秒＋後 1 秒、無音訊

表單內會顯示資料用途聲明；內容可由 Settings 自訂。請依發行地區及資料內容完成實際隱私／同意流程。

## 錄影限制

`Enable Rolling Video` 預設關閉。啟用後會持續低頻擷取並保留帶有 realtime timestamp 的 JPEG frame ring buffer，換取 Player build 也能保留事件發生前畫面。Unity Recorder 是 Editor-only，不能解決正式 Player 的回溯錄影。

macOS Editor／Standalone Player 使用套件內的 Universal Binary（Apple Silicon + Intel）和 AVAssetWriter 產生 H.264 MP4。Windows Editor／64-bit Standalone Player 使用 Windows 內建 Media Foundation H.264 encoder，並以 WIC 解碼 rolling JPEG frame；不需要 ffmpeg 或隨 Player 安裝額外 codec。兩者的 MIME type 都是 `video/mp4`，且會把 MP4 metadata 寫在 media data 前面，方便 Slack／瀏覽器提早建立預覽。

影片在背景 thread 完成，先寫進 `Application.temporaryCachePath`，建立 report 時再交易式複製到 PendingReports，Slack 則使用 `UploadHandlerFile` 直接由檔案上傳，避免另一份完整影片常駐 managed heap。

`Prefer Mp4` 預設開啟。若目前平台尚無 MP4 backend，或 macOS 的 H.264 encoder 暫時不可用，`Allow Legacy Avi Fallback` 可讓回報退回純 C# MJPEG AVI，而不是整份報告失敗。目前正式 MP4 backend 支援矩陣：

| 平台 | Runtime 影片輸出 |
|---|---|
| macOS Editor | H.264 MP4；可選 AVI fallback |
| macOS Standalone (Intel / Apple Silicon) | H.264 MP4；可選 AVI fallback |
| Windows Editor (x64) | H.264 MP4；可選 AVI fallback |
| Windows Standalone (x64) | H.264 MP4；可選 AVI fallback |
| iOS device／Simulator | H.264 MP4；可選 AVI fallback |
| Linux / Android | 目前使用 AVI fallback；介面已保留平台 encoder 擴充點 |
| WebGL | 建議停用 rolling video |

macOS native source 位於 `Native~/macOS`，執行 `build.sh` 可重建 `Runtime/Plugins/macOS/MacacaBeaconVideo.bundle`。它只連結 Apple 系統 framework，沒有額外第三方 runtime dependency。

Windows native source 位於 `Native~/Windows`。在裝有 Visual Studio 2022「Desktop development with C++」與 Windows 10/11 SDK 的 Windows 主機執行 `build.ps1`，即可重建 `Runtime/Plugins/Windows/x86_64/MacacaBeaconVideoWindows.dll`；macOS package 維護者也可安裝 MinGW-w64 後執行 `build-cross.sh`。正式支援 Windows 10/11 x64；32-bit Windows、UWP 與 ARM64 目前不在 PluginImporter 支援範圍。

iOS 使用 `Runtime/Plugins/iOS/MacacaBeaconVideo.mm`，由 Unity 產生 Xcode project 時直接編入，透過 `DllImport("__Internal")` 呼叫 AVAssetWriter。它使用 iOS 硬體 H.264 路徑，並由 PluginImporter 自動加入 AVFoundation、CoreGraphics、CoreMedia、CoreVideo、ImageIO 與 VideoToolbox；不需要在 Scene 放置額外元件。

其他平台可以實作 `IVideoEncoderBackend`，並在遊戲初始化時呼叫 `BugReporter.SetVideoEncoder(backend)`。Backend 會收到含 JPEG bytes 與 realtime timestamp 的唯讀 frame list，負責將結果寫到指定路徑；成功後套件會自動接手本地 staging、Slack 上傳與清理。

每個 frame 會記錄 double precision realtime timestamp，歷史緩衝依秒數而非 frame 數裁切。MP4 使用各 frame 的實際 presentation timestamp，並將最後一幀延伸到設定的 incident end；AVI fallback 也依實際捕捉時長產生時基。裝置無法達到設定 FPS 時只會降低流暢度，不會再把 8 秒內容加速成較短影片。若 Play Mode／Player 啟動尚未滿 `Seconds Before`，則只能保留啟動後實際存在的歷史畫面。

為避免記憶體／Slack 上傳失控，每個附件受 `Maximum Attachment Megabytes` 限制。影片不包含音訊，UI 在後段畫面收集與影片封裝完成前會暫時停用 Send。

## 本地失敗備援

`Save Failed Reports Locally` 預設開啟。送出時會先將 `report.txt`、截圖、影片與 diagnostics 暫存到 `Application.persistentDataPath/MacacaBeacon/PendingReports`；Slack 全部成功後才刪除該資料夾。任何訊息／附件上傳失敗或程式在傳輸途中關閉，檔案都會保留供事後手動上傳，UI 與 Unity Console 會顯示實際路徑。預設只保留最近 20 份，可用 `Maximum Retained Local Reports` 調整。

## 自訂遊戲資料

```csharp
using MacacaGames.RuntimeBugReporter;

public sealed class GameBugContext : IBugReportDataProvider
{
    public void Collect(BugReport report)
    {
        report.Fields["Quest"] = QuestService.CurrentQuestId;
        report.Fields["Player Position"] = Player.Position.ToString();
    }
}

// 初始化時
BugReporter.RegisterDataProvider(new GameBugContext());
```

也可手動呼叫 `BugReporter.Open()`，或用 `BugReporter.SetTransport(customTransport)` 注入任何 `IBugReportTransport`。

## 分享至其他專案

### 方式 A：以 Git submodule 匯入（推薦內部／多專案共用）

在 Unity 專案的 repository 根目錄執行：

```bash
git submodule add https://github.com/MacacaGames/MacacaBeacon.git Packages/com.macacagames.beacon
git submodule update --init --recursive
```

如果公司 Git 只允許 SSH，也可以使用：

```bash
git submodule add git@github.com:MacacaGames/MacacaBeacon.git Packages/com.macacagames.beacon
```

確認 `Packages/com.macacagames.beacon/package.json` 存在後，回到 Unity 開啟／重新載入專案即可。這種方式會在主專案留下 `.gitmodules` 與一個 submodule commit 指標；請把兩者一起提交：

```bash
git add .gitmodules Packages/com.macacagames.beacon
git commit -m "Add Macaca Beacon package"
```

其他人第一次 clone 主專案時，使用：

```bash
git clone --recurse-submodules <your-game-repository-url>
```

若已經 clone 但資料夾是空的，執行：

```bash
git submodule update --init --recursive
```

要更新套件版本時，先在 submodule 取得指定 tag 或 commit，再把主專案的 submodule 指標提交：

```bash
git -C Packages/com.macacagames.beacon fetch --tags origin
git -C Packages/com.macacagames.beacon checkout v0.3.0
git add Packages/com.macacagames.beacon
git commit -m "Update Macaca Beacon"
```

`checkout` 後 submodule 顯示 detached HEAD 是正常的；版本由主專案記錄的 commit 決定。若要在套件 repository 內開發，先切換到自己的 branch，完成後再回主專案提交新的 submodule 指標。

### 方式 B：以 UPM Git URL 匯入

將 `com.macacagames.beacon` 保持在獨立 Git repository 後，其他 Unity 專案也可在 `Packages/manifest.json` 加入：

```json
"com.macacagames.beacon": "https://github.com/your-org/macaca-beacon.git?path=/com.macacagames.beacon#v0.1.0"
```

套件 managed runtime assembly 沒有第三方相依；需要 Unity 的 IMGUI、UnityWebRequest、ScreenCapture 與 ImageConversion built-in modules。macOS／iOS MP4 backend 使用系統內建的 AVFoundation、VideoToolbox、CoreMedia、CoreVideo、CoreGraphics 與 ImageIO frameworks；Windows MP4 backend 使用系統內建的 Media Foundation、Windows Imaging Component 與 COM。

## 參考

- [Slack `chat.postMessage`](https://docs.slack.dev/reference/methods/chat.postMessage)
- [Slack external file upload](https://docs.slack.dev/reference/methods/files.getUploadURLExternal/)
- [Unity User Reporting 概念](https://docs.unity.com/en-us/cloud-diagnostics/user-reporting/about-user-reporting)
- [PE 工具箱：遊戲內 Bug Reporter](https://qwe321qwe321qwe321.github.io/posts/13673/)
