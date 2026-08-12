# Macaca Beacon

> Capture the moment. Signal the issue.

![Macaca Beacon](Documentation~/Images/macaca-beacon-banner.png)

遊戲內、可抽離為 UPM 的 Bug Report 工具。表單與影片預覽使用 **IMGUI**；桌面與手機皆可顯示安全區角落的低干擾入口，桌面另可用 F6、iOS／Android 可用三指長按手勢開啟。工具會收集截圖、最近 log、裝置／build／場景資訊，並送到 Slack。

截圖預覽提供單層內建標注畫筆，圖片顯示後即可直接繪製，不需要進入另一個模式。工具列固定提供紅／黃／青三色、三種筆刷粗細、Undo、可復原的 Clear，以及重新截圖。完成的筆跡會合成進 Slack 與本地失敗備援所使用的 PNG。

回報頁面的左側 `SCREENSHOT`／`VIDEO` tabs 只負責切換截圖標注與事件影片回顧。影片準備完成後可點擊／觸碰畫面切換播放與暫停；游標移到畫面上、拖曳時間軸或影片暫停時，畫面底部會顯示可拉動的播放進度與時間，不另外顯示重複的 Play／Restart 按鈕。右側 `Attachments` 將 `Screenshot` 與 `Video` 兩個短選項放在同一排；亮色代表會附加、暗色代表不附加，尚未可用的媒體則維持 disabled。這不會修改全域 screenshot capture 或 rolling-video 設定。非互動標題與提示文字不會在游標經過時顯示選取效果。影片畫面由 `VideoPlayer` 的 API-only texture 直接交給 IMGUI，不建立 RawImage、影片 Canvas、UGUI prefab 或中間 RenderTexture，也不會自動播放。

## 專案內啟用

這個 repository 已將套件放在 `Packages/com.macacagames.beacon`，Unity 會把它視為 embedded package。桌面進入 Play Mode 後直接按 **F6** 即可開啟；不需要在 Scene 放 prefab。勾選 `Show Entry Button` 後，桌面與手機都會顯示一個只佔自身矩形的小型 `!` 入口；iOS／Android 也可以用三指按住約 0.75 秒開啟。

第一次設定請開啟：

`Tools > Macaca Beacon > Open Settings`

設定資產會建立於 `Assets/Resources/BugReporterSettings.asset`。

`Enable In Build` 只控制 Player build 是否啟用 Beacon；Editor Play Mode 永遠可以使用目前設定進行開發與驗證。Player 關閉此選項後不會建立 runtime controller、收集 log 或 rolling video，F6、入口、手勢、`BugReporter.Open()` 與影片錄製 API 也不會啟動。若只想隱藏角落按鈕，關閉 `Show Entry Button` 即可；其他已啟用的入口仍可使用。

## Production 隔離

若要在 Production build 中停用 Beacon，於該 build target 的 `Project Settings > Player > Scripting Define Symbols` 加入：

`MACACA_BEACON_PRODUCTION`

這會在編譯期將設定欄位替換為停用的 shell，並停用自動啟動、`BugReporter.Open()` 與影片錄製切換；不需要額外 CI 設定。未加入此 define 時，Editor 維持完整 Beacon 行為，Player 則依 `Enable In Build` 決定是否啟用。

`Appearance` 預設使用全螢幕並將介面縮放設為 1.25，寬螢幕採左右雙欄，小尺寸 Game View 自動切換成可捲動單欄。關閉 `Fullscreen` 後會改用置中視窗，並可調整背景遮罩透明度與桌面視窗寬度比例。

跨平台角落入口位於 `Entry Button`：桌面與手機分別設定基礎尺寸，並共用透明度與角落位置。桌面入口會在 `!` 旁顯示目前設定的 `Shortcut` 按鍵；手機維持單一觸控按鈕。按鈕會把 `Screen.safeArea` 轉為 IMGUI 座標後避開瀏海與 Home indicator，只有指標或觸控落在按鈕自己的矩形內時才會攔截事件。三指設定獨立放在 `Mobile Gesture`，不受 `Show Entry Button` 影響。

## 軟體游標與掌機

桌面遊戲若在 BugReporter 開啟期間隱藏或鎖定 Unity Cursor，回報頁面會用 IMGUI 繪製自己的軟體游標，並以同一位置處理 hover、點擊、拖曳、頁面 Scrollbar、截圖標注與影片 seek。專案有安裝 Input System 時，locked 模式會透過條件式 adapter 讀取 `Mouse.current.delta`；未安裝時仍可編譯並使用 IMGUI delta fallback。BugReporter 不會寫入或還原 `Cursor.visible`／`Cursor.lockState`，也不會暫停遊戲或攔截專案直接讀取的滑鼠輸入。

iOS／Android、Unity 回報為 `DeviceType.Handheld` 或 `DeviceType.Console` 的裝置不會啟用桌面軟體游標。Steam Deck 等可能被 Unity 分類為 Desktop 的掌機，由宿主使用既有平台層判斷後啟用 Handheld Mode。它會改用 Mobile Entry Button 尺寸、隱藏 EntryButton 旁的鍵盤 Shortcut，並停用桌面軟體游標；實際鍵盤快捷鍵仍可使用。這個呼叫不會建立 BugReporter controller，也不會讓套件相依 Steamworks：

```csharp
using MacacaGames.RuntimeBugReporter;

// 由宿主既有的平台層判斷 Steam Deck／其他掌機後呼叫。
BugReporter.SetHandheldMode(true);
```

Steam 專案可以在自己的 Steamworks 初始化完成後，以 `SteamUtils.IsSteamRunningOnSteamDeck()` 的結果設定 Handheld Mode；Macaca Beacon 本身不引用或封裝 Steamworks。若只想個別控制軟體游標，仍可使用 `BugReporter.SetSoftwareCursorEnabled(...)`。

## Slack 設定

1. 建立 Slack App，替 Bot 加入 `chat:write` 與 `files:write` scopes，然後安裝／重新安裝 App。
2. 將 `Bot Token` 與目標 `Channel ID` 填入 Macaca Beacon Settings。
3. 邀請該 App 加入目標 channel。

主回報固定由 Bot 使用 `chat.postMessage` 發送並取得父訊息 `ts`；主訊息第一行固定為 `🐒 [BugReport]`，第二行顯示 `【Category】`、標題與描述，方便 Slack 自動化使用文字條件觸發。請固定比對 `[BugReport]`，不要依賴 emoji。完整的 ID、分類、建置、場景、時間、裝置資訊與自訂欄位會另發到該回報的 Thread。附件再依 Slack 官方流程走 `files.getUploadURLExternal` → 檔案位元組上傳 → 帶有 `thread_ts` 的 `files.completeUploadExternal`，因此截圖、影片與 diagnostics 也都位於主回報的 Thread。套件不使用 Incoming Webhook。

> 安全性：Bot Token 放進 Player 後可被擷取。僅建議內部／受信任測試使用。公開玩家版本應實作自己的 rate-limited relay，並以 `BugReporter.SetTransport(...)` 替換內建 transport；不要把 Slack secret 發佈給玩家。

## 會送出的資料

- 使用者輸入：分類、標題、描述、選填聯絡資訊
- PNG 截圖（按 F6、角落入口或三指手勢後、面板出現前擷取）
- 截圖上的選填畫筆標注
- Product、version、build GUID、Unity、platform、OS、CPU、RAM、GPU、VRAM、resolution、scene
- 有固定容量上限的 recent log ring buffer；Error／Exception 包含 stack trace
- 不受 recent log 淘汰影響的錄影關鍵 timeline，包含 backend、fallback 錯誤、輸出尺寸、frame 數、時長與有效 FPS
- 選填影片：macOS／Windows／Android／iOS 優先 H.264 MP4，預設 6 FPS、前 8 秒＋後 1 秒、無音訊

表單內會顯示資料用途聲明；內容可由 Settings 自訂。請依發行地區及資料內容完成實際隱私／同意流程。

## 錄影限制

`Enable Rolling Video` 預設關閉。macOS、iOS、Android 與 Windows 會依平台優先使用 GPU readback/native encoder；WebGL 則刻意使用 CPU JPEG capture，避開瀏覽器的 `AsyncGPUReadback` fence 問題，再交給瀏覽器 WebCodecs 做最後的 H.264 編碼。事件完成編碼或 runtime 關閉錄影後，rolling frame cache 會自動清除。

`Maximum Video Cache Megabytes` 預設為 512 MB。`Video Width` 是錄影輸出寬度，不是螢幕原生寬度；例如 Steam Deck 的 1280×800 畫面在 960 設定下會先擷取完整 backbuffer，再等比例縮成 960×600，以控制 readback、cache、編碼與附件成本，不會取出 960×600 區域造成裁切。這個明確縮放只用在 generic fallback capture；正常 macOS／Windows GPU recorder 維持既有路徑。直式 960 px、6 FPS、8 秒歷史通常需要比橫式畫面更多 temporary space；raw cache 超過上限時會先丟棄最舊 frame，因此低儲存空間裝置的實際保留秒數可能短於 `Seconds Before`。啟動事件時會在 log 顯示 requested 與 available 秒數。這個限制只影響 temporary raw cache，最終 MP4 仍由 bitrate 與附件大小限制控制。

macOS Editor／Standalone Player 與 iOS device／Simulator 會優先使用 Metal texture → IOSurface-backed CVPixelBuffer 的 GPU 路徑，再由 AVAssetWriter 產生 H.264 MP4；若 Apple GPU bridge 不可用，才回到 `AsyncGPUReadback` 的 CPU/native 路徑。macOS 使用套件內的 Universal Binary（Apple Silicon + Intel），iOS 則由 Unity 產生 Xcode project 時直接編入相同的 Apple native implementation。Windows Editor／64-bit Standalone Player 使用 Windows 內建 Media Foundation H.264 encoder。Windows GPU session 只有在 native pointer 有效且沒有初始化錯誤時才會啟用；若建立、送幀、segment finalization 或 incident merge 後續失敗，會釋放 GPU recorder 並在同一 runtime session 改用 generic recorder。Windows 與 Apple CPU fallback 直接接受 RGBA frame，不再經過 JPG encode/decode；不需要 ffmpeg 或隨 Player 安裝額外 codec。MIME type 都是 `video/mp4`，且會把 MP4 metadata 寫在 media data 前面，方便 Slack／瀏覽器提早建立預覽。

Windows build 經 Proton 執行時仍屬於 `UNITY_STANDALONE_WIN`，因此會先嘗試同一套 D3D11／D3D12 GPU recorder，而不是 native Linux backend。若 Proton 的 Media Foundation、D3D video processor、DXGI device manager 或 D3D12 shared-resource interop 無法完成，Macaca Beacon 會依實際錯誤切換 generic recorder，不需要偵測 Steam Deck 或引用 Steamworks。切換後的 rolling history 會重新累積；若 report 正在等待影片，會以切換當下開始的新 incident window 產生 partial recovery clip，無法補回 GPU backend 失敗前尚未完成的 frame。

### Steam Deck／Proton 錄影診斷

不帶診斷參數時仍使用正式的 `auto` 行為：健康的 Windows GPU recorder 維持 native texture → H.264 MP4，不增加 per-frame 診斷成本，也不降低解析度、FPS 或 bitrate。以下模式只用於分層測試，透過 Steam 遊戲內容的 Launch Options 傳入；強制模式失敗後刻意不再嘗試其他 encoder，避免掩蓋真正故障層：

| 測試 | Steam Launch Options | 預期用途 |
| --- | --- | --- |
| DX11 GPU | `PROTON_LOG=1 DXVK_HUD=devinfo,fps %command% -force-d3d11 -macaca-beacon-video-backend=windows-gpu` | 測 D3D11 Video Processor、DXGI manager 與 GPU Media Foundation input |
| DX12 GPU | `PROTON_LOG=1 VKD3D_DEBUG=warn %command% -force-d3d12 -macaca-beacon-video-backend=windows-gpu` | 額外測 D3D12 shared texture、fence 與 D3D11 interop |
| Windows CPU Media Foundation | `PROTON_LOG=1 %command% -macaca-beacon-video-backend=windows-cpu` | 排除 GPU／DXGI input，只測 RGBA frame → Media Foundation H.264 MP4 |
| Managed AVI | `PROTON_LOG=1 %command% -macaca-beacon-video-backend=managed-avi` | 排除 Media Foundation，測 generic capture、RGBA → JPEG、AVI 與 report attachment |

每次啟動後送出一份包含 Video 的 report。Slack 的 `diagnostics-*.txt` 會在一般 Recent logs 之前保留獨立的 `Video recording` 區段：列出螢幕與實際輸出尺寸、設定寬度、frame 數、時長、有效 FPS，以及不受 recent-log 上限淘汰的 backend／fallback 關鍵 timeline。開頭會列出 `mode`、實際 `selected` backend、renderer、GPU、OS 與 Unity version；失敗時會列出具體 D3D／Media Foundation operation 與 HRESULT。若仍需完整外部紀錄，Steam 的 Proton log 預設為 home 目錄下的 `steam-$APPID.log`；Windows Unity Player log 通常位於 Steam library 的 `steamapps/compatdata/$APPID/pfx/drive_c/users/steamuser/AppData/LocalLow/<CompanyName>/<ProductName>/Player.log`。

判讀方式：DX11 成功但 DX12 失敗，問題集中在 D3D12 interop；兩個 GPU 模式失敗但 `windows-cpu` 成功，問題集中在 Video Processor／DXGI GPU input；`windows-cpu` 也失敗但 `managed-avi` 成功，表示 Proton 的 Media Foundation H.264 路徑不可用；連 `managed-avi` 都失敗時，應改查 generic capture、temporary cache 或 report attachment，而不是 GPU encoder。

影片先寫進 `Application.temporaryCachePath`，建立 report 時再交易式複製到 PendingReports，Slack 則使用 `UploadHandlerFile` 直接由檔案上傳，避免另一份完整影片常駐 managed heap。Windows 與 Apple backend 在背景 thread 完成；Android 由 Unity main thread 啟動 Java encode job，實際檔案讀取、RGBA → YUV420 與 MediaCodec finalization 都在低優先序 Java worker 執行。回報表單在背景編碼期間仍可正常輸入，Send 會等影片 ready 後才開放。

`Prefer Mp4` 預設開啟。若 generic recorder 所在平台尚無 MP4 backend，或 CPU/native H.264 encoder 暫時不可用，`Allow Legacy Avi Fallback` 可讓回報退回 managed MJPEG AVI，而不是失去這次影片。既有 JPEG frame 會直接封裝；disk-backed RGBA frame 則只在 fallback finalization 時使用 Unity thread-safe Image Conversion 與 `Video Jpeg Quality` 轉成 JPEG，不會把持續錄影改成每幀即時壓縮。目前正式 MP4 backend 支援矩陣：

頁面內所有 H.264 MP4 與 AVI 都會先使用 Unity `VideoPlayer`，所以原生 Windows 能正常播放 managed AVI 時維持原路徑。只有 MacacaBeacon 自己產生的 managed MJPEG AVI 在 `VideoPlayer` 回報錯誤，或解出的時長、frame 數、畫面比例與錄影結果明顯不一致時，才直接讀取容器內 JPEG frames；這能避開 Proton 將 9 秒、960×600 AVI 誤解成 4 秒測試色條。managed fallback 只在 Report 的 VIDEO tab 播放或拖曳時進行 CPU JPEG decode，不影響背景 rolling capture、正常遊戲或 MP4。managed reader 也無法解析時，VIDEO tab 會顯示無法預覽、diagnostics 保留錯誤，但有效檔案仍可勾選並發送。影片沒有音訊。這些行為不需要 Steamworks、Steam Deck 偵測、`SetHandheldMode` 或宿主專案設定。

| 平台 | Runtime 影片輸出 |
|---|---|
| macOS Editor | H.264 MP4；可選 AVI fallback |
| macOS Standalone (Intel / Apple Silicon) | H.264 MP4；可選 AVI fallback |
| Windows Editor (x64) | H.264 MP4；可選 AVI fallback |
| Windows Standalone (x64) | H.264 MP4；可選 AVI fallback |
| Windows Standalone via Proton | Windows GPU H.264 成功時使用 MP4；GPU 失敗後改走 generic MP4，仍失敗時可退回 managed AVI |
| iOS device／Simulator | Metal GPU + AVAssetWriter H.264 MP4；GPU 不可用時回退 CPU；可選 AVI fallback |
| Android device | H.264 MP4（MediaCodec／MediaMuxer）；可選 AVI fallback |
| Linux／Steam Deck native Player | Managed MJPEG AVI fallback（RGBA → JPEG）；介面已保留平台 encoder 擴充點 |
| WebGL | WebCodecs H.264 + 內建 MP4 muxer；不支援時可選 AVI fallback |

macOS native source 位於 `Native~/macOS`，執行 `build.sh` 可重建 `Runtime/Plugins/macOS/MacacaBeaconVideo.bundle`。它只連結 Apple 系統 framework，沒有額外第三方 runtime dependency。

Windows native source 位於 `Native~/Windows`。在裝有 Visual Studio 2022「Desktop development with C++」與 Windows 10/11 SDK 的 Windows 主機執行 `build.ps1`，即可重建 `Runtime/Plugins/Windows/x86_64/MacacaBeaconVideoWindows.dll`；macOS package 維護者也可安裝 MinGW-w64 後執行 `build-cross.sh`。正式支援 Windows 10/11 x64；32-bit Windows、UWP 與 ARM64 目前不在 PluginImporter 支援範圍。

iOS 使用 `Runtime/Plugins/iOS/MacacaBeaconVideo.mm`，由 Unity 產生 Xcode project 時直接編入，透過 `DllImport("__Internal")` 呼叫 Metal render-event bridge 與 AVAssetWriter。GPU 路徑直接把 Unity Metal texture blit 到 IOSurface-backed CVPixelBuffer，並使用 iOS 硬體 H.264 路徑；PluginImporter 會加入 AVFoundation、CoreGraphics、CoreMedia、CoreVideo、ImageIO、Metal 與 VideoToolbox。不需要在 Scene 放置額外元件。

Android 使用 `Runtime/Plugins/Android/MacacaBeaconVideo.java`，透過 Android 內建的 `MediaCodec` 與 `MediaMuxer` 將 RGBA frame 轉換為 encoder 支援的 YUV420 並編碼為 H.264 MP4；不需要額外安裝 ffmpeg 或加入錄影權限。

其他平台可以實作 `IVideoEncoderBackend`，並在遊戲初始化時呼叫 `BugReporter.SetVideoEncoder(backend)`。Backend 會收到含 frame format、尺寸、temporary data path 與 realtime timestamp 的唯讀 frame list，並可用 `ReadData()` 逐張載入，避免一次載入整段影片；成功後套件會自動接手本地 staging、Slack 上傳與清理。

每個 frame 會記錄 double precision realtime timestamp，歷史緩衝依秒數而非 frame 數裁切。MP4 使用各 frame 的實際 presentation timestamp，並將最後一幀延伸到設定的 incident end；AVI fallback 也依實際捕捉時長產生時基。裝置無法達到設定 FPS 時只會降低流暢度，不會再把 8 秒內容加速成較短影片。若 Play Mode／Player 啟動尚未滿 `Seconds Before`，則只能保留啟動後實際存在的歷史畫面。

WebGL 使用瀏覽器原生 WebCodecs H.264 encoder，再由套件內的 MP4 muxer 封裝輸出；不使用 WebGPU 直接編碼，也不需要在遊戲啟動時下載 ffmpeg。瀏覽器必須支援 H.264 `VideoEncoder`、`VideoFrame` 與 `createImageBitmap`，且通常需要 HTTPS 或 localhost。若瀏覽器不支援 WebCodecs，`Allow Legacy Avi Fallback` 開啟時會退回 managed MJPEG AVI。

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

Editor Play Mode 可直接呼叫 `BugReporter.Open()`；Player 則需啟用 `Enable In Build`。也可用 `BugReporter.SetTransport(customTransport)` 注入任何 `IBugReportTransport`。

Rolling video 也提供 runtime API，可在遊戲內依效能或隱私需求切換；切換不會修改 Settings asset：

```csharp
using MacacaGames.RuntimeBugReporter;

BugReporter.SetVideoRecordingEnabled(false);
bool enabled = BugReporter.IsVideoRecordingEnabled;
```

若專案使用 SRDebugger，可從 Unity Package Manager 匯入 `SRDebugger Integration` sample。匯入後，`SROptions.MacacaBeacon.cs` 會被複製到 `Assets`，將同一個開關放入 `SRDebugger > Options > Macaca Beacon`。這個整合檔不會由 Beacon runtime 自動編譯，因此 Beacon package 本身不引用 SRDebugger，也不會產生 assembly dependency。

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
git -C Packages/com.macacagames.beacon checkout v0.5.0
git add Packages/com.macacagames.beacon
git commit -m "Update Macaca Beacon"
```

`checkout` 後 submodule 顯示 detached HEAD 是正常的；版本由主專案記錄的 commit 決定。若要在套件 repository 內開發，先切換到自己的 branch，完成後再回主專案提交新的 submodule 指標。

### 方式 B：以 UPM Git URL 匯入

將 `com.macacagames.beacon` 保持在獨立 Git repository 後，其他 Unity 專案也可在 `Packages/manifest.json` 加入：

```json
"com.macacagames.beacon": "https://github.com/MacacaGames/MacacaBeacon.git#v0.5.0"
```

`v0.5.0` 是目前的穩定版本 tag。更新到其他版本時，將 URL 最後的 tag 替換成指定版本，例如 `#v0.4.0`。由於 package 位於 repository 根目錄，Git URL 不需要 `path` 參數。

套件 managed runtime assembly 沒有第三方相依；需要 Unity 的 IMGUI、Video、UnityWebRequest、ScreenCapture 與 ImageConversion built-in modules。macOS／iOS MP4 backend 使用系統內建的 AVFoundation、VideoToolbox、CoreMedia、CoreVideo 與 Metal frameworks；Windows MP4 backend 使用系統內建的 Media Foundation 與 COM；Android MP4 backend 使用系統內建的 MediaCodec 與 MediaMuxer。ImageIO、WIC 與 Bitmap APIs 僅保留給 JPEG compatibility fallback。

## 參考

- [Slack `chat.postMessage`](https://docs.slack.dev/reference/methods/chat.postMessage)
- [Slack external file upload](https://docs.slack.dev/reference/methods/files.getUploadURLExternal/)
- [Unity User Reporting 概念](https://docs.unity.com/en-us/cloud-diagnostics/user-reporting/about-user-reporting)
- [PE 工具箱：遊戲內 Bug Reporter](https://qwe321qwe321qwe321.github.io/posts/13673/)
