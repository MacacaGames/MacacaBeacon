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

若要在 Production build 中停用 Beacon，於該 build target 的 `Project Settings > Player > Scripting Define Symbols` 加入以下任一個 define：

`MACACA_BEACON_PRODUCTION` 或 `PRODUCTION`

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
- 選填影片：macOS／Windows／Android／iOS 優先 H.264 MP4，預設 30 FPS、前 5 秒＋後 1 秒、無音訊

表單內會顯示資料用途聲明；內容可由 Settings 自訂。請依發行地區及資料內容完成實際隱私／同意流程。

## 錄影限制

`Enable Rolling Video` 預設關閉。macOS、iOS、Android 與 Windows 會依平台優先使用原生 GPU／硬體 encoder；WebGL 則刻意使用 CPU JPEG capture，避開瀏覽器的 `AsyncGPUReadback` fence 問題，再交給瀏覽器 WebCodecs 做最後的 H.264 編碼。事件完成編碼或 runtime 關閉錄影後，rolling frame cache 會自動清除。

### Windows GPU 錄影（DX11／DX12）

原生 Windows 使用 Media Foundation H.264 MP4。DX12 使用 Unity D3D12 resource interop；DX11 則將 Unity 的 D3D11 texture 複製到同一個 adapter 上的 shared keyed-mutex texture，再交給獨立的 D3D11 encoder device/context 執行 Media Foundation 與 H.264 編碼。Unity render thread 只提交 GPU copy，不執行 GPU → CPU readback，也不會讓 Media Foundation 直接使用 Unity 的 immediate context。

若要在 Windows Editor 中強制只使用這條原生 GPU 路徑，加入：

```properties
-macaca-beacon-video-backend=windows-gpu
```

強制 `windows-gpu` 時，GPU 初始化或錄影失敗會停用該次錄影，不會改用 generic MP4／AVI compatibility recorder。未指定 backend 時使用 `auto`；`auto` 才保留既有的相容性恢復行為。Standalone Player 不讀取這個 Editor 診斷參數，原生 Windows 會自動選擇 GPU backend，Proton 則使用獨立的 OpenH264 軟體路徑。

GPU smoke test 可使用一般 Unity Editor 執行：

```properties
Unity.exe -projectPath ProjectName.Unity -force-d3d11 -macacaWindowsGpuSmoke -executeMethod MacacaGames.RuntimeBugReporter.Editor.WindowsGpuVideoSmoke.Run
```

將 `-force-d3d11` 改為 `-force-d3d12` 可驗證 DX12 路徑。GPU smoke test 不可搭配 `-nographics`，因為該參數會建立 Null graphics device，而不是 D3D11／D3D12 device。

`Maximum Video Cache Megabytes` 預設為 512 MB。`Video Width` 是錄影輸出寬度，不是螢幕原生寬度；例如 Steam Deck 的 1280×800 畫面在 960 設定下會先擷取完整 backbuffer，再等比例縮成 960×600，以控制 readback、cache、編碼與附件成本，不會取出 960×600 區域造成裁切。Windows／Proton generic recorder 啟動時會依寬高、前後秒數與 FPS 一次配置固定大小的 persistent `NativeArray` ring；若需求超過 cache 上限，會先降低有效 capture FPS，確保整個 incident window 與少量 in-flight slots 永遠落在預留區內。以 960×600、30 FPS、前 5 秒＋後 1 秒計算，raw ring 約使用 422 MiB。rolling 過程不建立每幀 `.rgba` 檔，也不在 managed heap 留下一整段 `byte[]`；只有回報頁面開啟、後段畫面收集完成後才開始編碼並寫出最終 MP4。若 `AsyncGPUReadback` 不可用或 RAM ring 配置失敗，仍保留原本 disk-backed generic fallback。

macOS Editor／Standalone Player 與 iOS device／Simulator 會優先使用 Metal texture → IOSurface-backed CVPixelBuffer 的 GPU 路徑，再由 AVAssetWriter 產生 H.264 MP4；若 Apple GPU bridge 不可用，才回到 `AsyncGPUReadback` 的 CPU/native 路徑。macOS 使用套件內的 Universal Binary（Apple Silicon + Intel），iOS 則由 Unity 產生 Xcode project 時直接編入相同的 Apple native implementation。Windows Editor 保留 Media Foundation 與各 backend 的開發診斷能力；Windows Standalone Player 會自動辨識 Proton，Proton 使用 NativeArray RAM ring + deferred OpenH264，原生 Windows 維持 GPU／Media Foundation 路徑。MIME type 是 `video/mp4`，並把 MP4 metadata 寫在 media data 前面，方便 Slack／瀏覽器提早建立預覽。

Windows build 經 Proton 執行時仍屬於 `UNITY_STANDALONE_WIN`。Standalone 啟動時會檢查 `ntdll.dll` 是否匯出 `wine_get_version`；命中後自動選擇 generic RAM ring + in-process OpenH264，不讀取 backend Launch Option，也不嘗試 Media Foundation 或 D3D11／D3D12 shared-resource interop。Proton readback 已是顯示順序，因此不再套用原生 Windows D3D 的額外垂直翻轉。rolling 階段只有 GPU readback 到預配置 RAM；Beacon UI 開啟後才在 background worker 將 RGBA 轉為 I420、由 OpenH264 軟體編碼並在同一個 native DLL 內封裝 fast-start MP4。若 OpenH264 失敗且 `Allow Legacy Avi Fallback` 開啟，仍可退回 managed AVI，但不會改走 Media Foundation。

### Steam Deck／Proton 錄影診斷

Steam Deck 的 Windows Standalone build 不需要 backend Launch Option，直接以 `%command%` 啟動；偵測到 Proton 後會使用 NativeArray RAM ring + in-process OpenH264。`-macaca-beacon-video-backend` 目前只保留給 Windows Editor 做分層開發診斷，Player 會忽略它。實機測試可由 diagnostics 的 `mode=software-mp4, selected=software-mp4` 確認自動偵測成功。

Beacon 開啟時的單張 PNG 截圖在 Proton 下直接使用 Unity 回傳的當下 backbuffer texture，不依賴可能與 Gamescope viewport 暫時不同步的 `Screen.width`／`Screen.height` 建立畫布；這可避免偶發出現遊戲內容只佔截圖畫布一部分。此路徑只執行一次，不影響 rolling capture FPS。

每次啟動後送出一份包含 Video 的 report。Slack 的 `diagnostics-*.txt` 會在一般 Recent logs 之前保留獨立的 `Video recording` 區段：列出螢幕與實際輸出尺寸、設定寬度、frame 數、時長、有效 FPS，以及不受 recent-log 上限淘汰的 backend／fallback 關鍵 timeline。開頭會列出 `mode`、實際 `selected` backend、renderer、GPU、OS 與 Unity version；失敗時會列出具體 D3D／Media Foundation operation 與 HRESULT。若仍需完整外部紀錄，Steam 的 Proton log 預設為 home 目錄下的 `steam-$APPID.log`；Windows Unity Player log 通常位於 Steam library 的 `steamapps/compatdata/$APPID/pfx/drive_c/users/steamuser/AppData/LocalLow/<CompanyName>/<ProductName>/Player.log`。

最終影片寫進 `Application.temporaryCachePath`，建立 report 時再交易式複製到 PendingReports，Slack 則使用 `UploadHandlerFile` 直接由檔案上傳，避免另一份完整影片常駐 managed heap。Windows rolling raw frames 在 finalization 前只存在預配置 RAM ring；Windows 與 Apple backend 在背景 thread 完成。Android 由 Unity main thread 啟動 Java encode job，實際檔案讀取、RGBA → YUV420 與 MediaCodec finalization 都在低優先序 Java worker 執行。回報表單在背景編碼期間仍可正常輸入，Send 會等影片 ready 後才開放。

`Prefer Mp4` 預設開啟。若 generic recorder 所在平台尚無 MP4 backend，或 CPU/native H.264 encoder 暫時不可用，`Allow Legacy Avi Fallback` 可讓回報退回 managed MJPEG AVI，而不是失去這次影片。既有 JPEG frame 會直接封裝；disk-backed RGBA frame 則只在 fallback finalization 時使用 Unity thread-safe Image Conversion 與 `Video Jpeg Quality` 轉成 JPEG，不會把持續錄影改成每幀即時壓縮。目前正式 MP4 backend 支援矩陣：

頁面內所有 H.264 MP4 與 AVI 都會先使用 Unity `VideoPlayer`，所以原生 Windows 能正常播放 managed AVI 時維持原路徑。只有 MacacaBeacon 自己產生的 managed MJPEG AVI 在 `VideoPlayer` 回報錯誤，或解出的時長、frame 數、畫面比例與錄影結果明顯不一致時，才直接讀取容器內 JPEG frames；這能避開 Proton 將 9 秒、960×600 AVI 誤解成 4 秒測試色條。managed fallback 只在 Report 的 VIDEO tab 播放或拖曳時進行 CPU JPEG decode，不影響背景 rolling capture、正常遊戲或 MP4。managed reader 也無法解析時，VIDEO tab 會顯示無法預覽、diagnostics 保留錯誤，但有效檔案仍可勾選並發送。影片沒有音訊。這些行為不需要 Steamworks、Steam Deck 偵測、`SetHandheldMode` 或宿主專案設定。

| 平台 | Runtime 影片輸出 |
|---|---|
| macOS Editor | H.264 MP4；可選 AVI fallback |
| macOS Standalone (Intel / Apple Silicon) | H.264 MP4；可選 AVI fallback |
| Windows Editor (x64) | Native D3D11／D3D12 GPU + Media Foundation H.264 MP4；強制 `windows-gpu` 失敗時不 fallback |
| Windows Standalone (x64) | Native D3D11／D3D12 GPU + Media Foundation H.264 MP4；`auto` 模式保留相容性恢復 |
| Windows Standalone via Proton | NativeArray RAM ring + deferred in-process OpenH264 MP4；失敗時可退回 managed AVI |
| iOS device／Simulator | Metal GPU + AVAssetWriter H.264 MP4；GPU 不可用時回退 CPU；可選 AVI fallback |
| Android device | H.264 MP4（MediaCodec／MediaMuxer）；可選 AVI fallback |
| Linux／Steam Deck native Player | Managed MJPEG AVI fallback（RGBA → JPEG）；介面已保留平台 encoder 擴充點 |
| WebGL | WebCodecs H.264 + 內建 MP4 muxer；不支援時可選 AVI fallback |

macOS native source 位於 `Native~/macOS`，執行 `build.sh` 可重建 `Runtime/Plugins/macOS/MacacaBeaconVideo.bundle`。它只連結 Apple 系統 framework，沒有額外第三方 runtime dependency。

Windows native source 位於 `Native~/Windows`。在裝有 Visual Studio 2022「Desktop development with C++」、Windows 10/11 SDK，並將 `OPENH264_ROOT` 指向 OpenH264 2.6.0 headers 與 `openh264.lib` 的 Windows 主機執行 `build.ps1`，即可重建 `Runtime/Plugins/Windows/x86_64/MacacaBeaconVideoWindows.dll`；Unity 執行時載入的是這個預編譯 DLL，不是直接載入 C++ 原始碼。macOS package 維護者也可安裝 MinGW-w64 後執行 `build-cross.sh`，腳本會抓取並驗證 pinned OpenH264 commit 後從 source 靜態連結。Player 不需額外 `openh264.dll`。OpenH264 的 BSD 授權與 H.264 patent notice 記錄在 `OPENH264-LICENSE.md`；發佈產品前仍應由產品方確認適用地區的專利授權義務。正式支援 Windows 10/11 x64；32-bit Windows、UWP 與 ARM64 目前不在 PluginImporter 支援範圍。

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

套件 managed runtime assembly 沒有第三方 managed 相依；需要 Unity 的 IMGUI、Video、UnityWebRequest、ScreenCapture、ImageConversion 與 Collections built-in modules。macOS／iOS MP4 backend 使用系統內建的 AVFoundation、VideoToolbox、CoreMedia、CoreVideo 與 Metal frameworks；Windows native plugin 使用系統內建的 Media Foundation／COM，並靜態連結 BSD-licensed OpenH264 作為 Proton deferred software fallback；Android MP4 backend 使用系統內建的 MediaCodec 與 MediaMuxer。ImageIO、WIC 與 Bitmap APIs 僅保留給 JPEG compatibility fallback。

## 參考

- [Slack `chat.postMessage`](https://docs.slack.dev/reference/methods/chat.postMessage)
- [Slack external file upload](https://docs.slack.dev/reference/methods/files.getUploadURLExternal/)
- [Unity User Reporting 概念](https://docs.unity.com/en-us/cloud-diagnostics/user-reporting/about-user-reporting)
- [PE 工具箱：遊戲內 Bug Reporter](https://qwe321qwe321qwe321.github.io/posts/13673/)
