# Macaca Beacon

> Capture the moment. Signal the issue.

遊戲內、可抽離為 UPM 的 Bug Report 工具。它使用 **IMGUI**（不依賴 UGUI），可用 F6 開啟，收集截圖、最近 log、裝置／build／場景資訊，並送到 Slack。

截圖預覽提供單層內建標注畫筆，圖片顯示後即可直接繪製，不需要進入另一個模式。工具列固定提供紅／黃／青三色、三種筆刷粗細、Undo、可復原的 Clear，以及重新截圖。完成的筆跡會合成進 Slack 與本地失敗備援所使用的 PNG。

## 專案內啟用

這個 repository 已將套件放在 `Packages/com.macacagames.beacon`，Unity 會把它視為 embedded package。進入 Play Mode 後直接按 **F6** 即可開啟；不需要在 Scene 放 prefab。

第一次設定請開啟：

`Tools > Macaca Beacon > Open Settings`

設定資產會建立於 `Assets/Resources/BugReporterSettings.asset`。

`Appearance` 預設使用全螢幕並將介面縮放設為 1.25，寬螢幕採左右雙欄，小尺寸 Game View 自動切換成可捲動單欄。關閉 `Fullscreen` 後會改用置中視窗，並可調整背景遮罩透明度與桌面視窗寬度比例。

## Slack 設定

1. 建立 Slack App，替 Bot 加入 `chat:write` 與 `files:write` scopes，然後安裝／重新安裝 App。
2. 將 `Bot Token` 與目標 `Channel ID` 填入 Macaca Beacon Settings。
3. 邀請該 App 加入目標 channel。

主回報固定由 Bot 使用 `chat.postMessage` 發送並取得父訊息 `ts`；附件再依 Slack 官方流程走 `files.getUploadURLExternal` → 檔案位元組上傳 → 帶有 `thread_ts` 的 `files.completeUploadExternal`，因此截圖、影片與 diagnostics 都位於主回報的 Thread。套件不使用 Incoming Webhook。

> 安全性：Bot Token 放進 Player 後可被擷取。僅建議內部／受信任測試使用。公開玩家版本應實作自己的 rate-limited relay，並以 `BugReporter.SetTransport(...)` 替換內建 transport；不要把 Slack secret 發佈給玩家。

## 會送出的資料

- 使用者輸入：分類、標題、描述、選填聯絡資訊
- PNG 截圖（按 F6 後、面板出現前擷取）
- 截圖上的選填畫筆標注
- Product、version、build GUID、Unity、platform、OS、CPU、RAM、GPU、VRAM、resolution、scene
- 有固定容量上限的 recent log ring buffer；Error／Exception 包含 stack trace
- 選填 MJPEG AVI：預設 6 FPS、前 5 秒＋後 5 秒、無音訊

表單內會顯示資料用途聲明；內容可由 Settings 自訂。請依發行地區及資料內容完成實際隱私／同意流程。

## 錄影限制

`Enable Rolling Video` 預設關閉。啟用後會持續低頻擷取、縮圖及 JPEG encode，換取 Player build 也能保留事件發生前畫面。Unity Recorder 是 Editor-only，不能解決正式 Player 的回溯錄影，因此套件使用自帶 MJPEG AVI writer。建議先以目標硬體量測；行動裝置可降到 3 FPS / 640px，或改接平台原生錄影實作。

每個 frame 會記錄 realtime timestamp，歷史緩衝依秒數而非 frame 數裁切，AVI 時基也依實際捕捉時長產生。裝置無法達到設定 FPS 時只會降低流暢度，不會再把 8 秒內容加速成較短影片。若 Play Mode／Player 啟動尚未滿 `Seconds Before`，則只能保留啟動後實際存在的歷史畫面。

為避免記憶體／Slack 上傳失控，每個附件受 `Maximum Attachment Megabytes` 限制。影片不包含音訊，UI 在後 5 秒收集完成前會暫時停用 Send。

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

將 `com.macacagames.beacon` 資料夾放到獨立 Git repository 後，其他 Unity 專案可在 `Packages/manifest.json` 加入：

```json
"com.macacagames.beacon": "https://github.com/your-org/macaca-beacon.git?path=/com.macacagames.beacon#v0.1.0"
```

套件 runtime assembly 沒有第三方相依；需要 Unity 的 IMGUI、UnityWebRequest、ScreenCapture 與 ImageConversion built-in modules。

## 參考

- [Slack `chat.postMessage`](https://docs.slack.dev/reference/methods/chat.postMessage)
- [Slack external file upload](https://docs.slack.dev/reference/methods/files.getUploadURLExternal/)
- [Unity User Reporting 概念](https://docs.unity.com/en-us/cloud-diagnostics/user-reporting/about-user-reporting)
- [PE 工具箱：遊戲內 Bug Reporter](https://qwe321qwe321qwe321.github.io/posts/13673/)
