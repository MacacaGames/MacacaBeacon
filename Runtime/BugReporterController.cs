using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class BugReporterController : MonoBehaviour
    {
        private static readonly string[] AnnotationColorLabels = { "RED", "YELLOW", "CYAN" };
        private static readonly string[] AnnotationSizeLabels = { "S", "M", "L" };

        internal static BugReporterController Instance { get; private set; }
        internal bool IsOpen { get; private set; }

        private BugReporterSettings settings;
        private RecentLogCollector logs;
        private RollingVideoRecorder videoRecorder;
        private byte[] screenshotBytes;
        private byte[] videoBytes;
        private Texture2D screenshotPreview;
        private ScreenshotAnnotator screenshotAnnotator;
        private bool isAnnotatingScreenshot;
        private int annotationColorIndex;
        private int annotationSizeIndex = 1;
        private string reporter = "";
        private string title = "";
        private string description = "";
        private int categoryIndex;
        private bool isOpening;
        private bool isSending;
        private string status = "";
        private bool statusIsError;
        private string validationMessage = "";
        private string pendingFocusControl;
        private Vector2 formScroll;
        private Rect windowRect;
        private CursorLockMode previousCursorLock;
        private bool previousCursorVisible;
        private GUIStyle titleStyle;
        private GUIStyle subtitleStyle;
        private GUIStyle labelStyle;
        private GUIStyle hintStyle;
        private GUIStyle fieldStyle;
        private GUIStyle areaStyle;
        private GUIStyle cardStyle;
        private GUIStyle buttonStyle;
        private GUIStyle categoryStyle;
        private GUIStyle primaryButtonStyle;
        private GUIStyle closeButtonStyle;
        private GUIStyle statusStyle;
        private GUIStyle validationStyle;
        private readonly List<Texture2D> styleTextures = new List<Texture2D>();
        private Texture2D windowTexture;
        private Texture2D accentTexture;
        private Texture2D fieldTexture;
        private float styleScale = -1f;

        internal void Initialize(BugReporterSettings value)
        {
            settings = value;
            logs = new RecentLogCollector(settings.maximumLogEntries);
            videoRecorder = new RollingVideoRecorder(this, settings);
            videoRecorder.Start();
        }

        internal void RequestOpen()
        {
            if (settings == null || IsOpen || isOpening)
                return;
            StartCoroutine(OpenAfterCapture());
        }

        internal void Close()
        {
            if (!IsOpen || isSending)
                return;
            IsOpen = false;
            Cursor.lockState = previousCursorLock;
            Cursor.visible = previousCursorVisible;
            status = "";
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            logs?.Dispose();
            if (screenshotPreview != null) Destroy(screenshotPreview);
            ReleaseStyleTextures();
        }

        private IEnumerator OpenAfterCapture()
        {
            isOpening = true;
            status = "Capturing context…";
            screenshotBytes = null;
            videoBytes = null;
            screenshotAnnotator = null;
            isAnnotatingScreenshot = false;
            videoRecorder.MarkIncident(bytes => videoBytes = bytes);

            if (settings.includeScreenshot)
            {
                yield return CaptureUtility.CapturePng((bytes, texture) =>
                {
                    screenshotBytes = bytes;
                    if (screenshotPreview != null) Destroy(screenshotPreview);
                    screenshotPreview = texture;
                    screenshotAnnotator = texture == null ? null : new ScreenshotAnnotator(texture);
                });
            }

            previousCursorLock = Cursor.lockState;
            previousCursorVisible = Cursor.visible;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            isOpening = false;
            IsOpen = true;
            pendingFocusControl = "BugReportTitle";
            status = settings.enableRollingVideo && settings.secondsAfter > 0
                ? "Recording the seconds after the incident…"
                : "Ready to send.";
        }

        private void OnGUI()
        {
            if (settings == null)
                return;

            var current = Event.current;
            if (current.type == EventType.KeyDown && current.keyCode == settings.shortcut)
            {
                if (IsOpen) Close(); else RequestOpen();
                current.Use();
            }
            if (!IsOpen)
                return;
            if (settings.allowEscapeToClose && current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape)
            {
                Close();
                current.Use();
                return;
            }

            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Return && (current.control || current.command))
            {
                TryBeginSend();
                current.Use();
            }

            var uiScale = Mathf.Clamp(Screen.height / 900f, 0.9f, 1.25f) * settings.interfaceScale;
            EnsureStyles(uiScale);
            GUI.depth = -10000;
            var overlay = new Rect(0, 0, Screen.width, Screen.height);
            GUI.color = new Color(0f, 0.02f, 0.04f, settings.backdropOpacity);
            GUI.DrawTexture(overlay, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float width;
            float height;
            if (settings.fullscreen)
            {
                width = Screen.width;
                height = Screen.height;
                windowRect = new Rect(0f, 0f, width, height);
            }
            else
            {
                var outerMargin = Mathf.Clamp(Mathf.Min(Screen.width, Screen.height) * 0.035f, 20f, 48f);
                width = Mathf.Min(Mathf.Clamp(Screen.width * settings.desktopWidthRatio, 760f, 1180f), Screen.width - outerMargin * 2f);
                height = Mathf.Min(Mathf.Clamp(Screen.height * 0.88f, 620f, 880f), Screen.height - outerMargin * 2f);
                windowRect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
            }
            windowRect = GUILayout.Window(GetInstanceID(), windowRect, DrawWindow, GUIContent.none, GUIStyle.none, GUILayout.Width(width), GUILayout.Height(height));

            if (!string.IsNullOrEmpty(pendingFocusControl) && Event.current.type == EventType.Repaint)
            {
                GUI.FocusControl(pendingFocusControl);
                pendingFocusControl = null;
            }
        }

        private void DrawWindow(int id)
        {
            GUI.DrawTexture(new Rect(0, 0, windowRect.width, windowRect.height), windowTexture);
            GUI.DrawTexture(new Rect(0, 0, Mathf.Max(4f, 4f * styleScale), windowRect.height), accentTexture);
            GUI.color = Color.white;
            GUILayout.BeginVertical(GUILayout.ExpandHeight(true));
            GUILayout.Space(18 * styleScale);
            GUILayout.BeginHorizontal();
            GUILayout.Space(28 * styleScale);
            GUILayout.BeginVertical();
            GUILayout.Label(settings.reportTitle, titleStyle);
            GUILayout.Label("Capture the moment. Signal the issue.", subtitleStyle);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            GUILayout.Label(settings.shortcut + "  toggle", hintStyle, GUILayout.Width(88 * styleScale));
            GUI.enabled = !isSending;
            if (GUILayout.Button("CLOSE", closeButtonStyle, GUILayout.Width(82 * styleScale), GUILayout.Height(44 * styleScale)))
                Close();
            GUI.enabled = true;
            GUILayout.Space(28 * styleScale);
            GUILayout.EndHorizontal();
            GUILayout.Space(18 * styleScale);

            if (windowRect.width >= 900f)
                DrawDesktopContent();
            else
                DrawCompactContent();

            GUILayout.Space(16 * styleScale);
            GUILayout.BeginHorizontal();
            GUILayout.Space(28 * styleScale);
            GUI.enabled = !isSending;
            if (GUILayout.Button("CANCEL", buttonStyle, GUILayout.Height(48 * styleScale), GUILayout.Width(120 * styleScale))) Close();
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label("Ctrl / Cmd + Enter", hintStyle, GUILayout.Width(142 * styleScale));
            var videoPending = settings.enableRollingVideo && videoRecorder.IsFinalizing;
            var canSend = !isSending && !videoPending;
            GUI.enabled = canSend;
            var sendLabel = isSending ? "SENDING…" : videoPending ? "FINISHING VIDEO…" : "SEND TO SLACK";
            if (GUILayout.Button(sendLabel, primaryButtonStyle, GUILayout.Height(48 * styleScale), GUILayout.Width(190 * styleScale)))
                TryBeginSend();
            GUI.enabled = true;
            GUILayout.Space(28 * styleScale);
            GUILayout.EndHorizontal();
            GUILayout.Space(22 * styleScale);
            GUILayout.EndVertical();
        }

        private void DrawDesktopContent()
        {
            var availableWidth = windowRect.width - 56f * styleScale;
            var leftWidth = availableWidth * 0.42f;
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            GUILayout.Space(28 * styleScale);
            GUILayout.BeginVertical(cardStyle, GUILayout.Width(leftWidth), GUILayout.ExpandHeight(true));
            DrawScreenshotPanel(isAnnotatingScreenshot
                ? Mathf.Clamp(windowRect.height * 0.48f, 280f, 460f)
                : Mathf.Clamp(windowRect.height * 0.34f, 210f, 320f));
            GUILayout.Space(16 * styleScale);
            DrawCaptureSummary();
            GUILayout.FlexibleSpace();
            GUILayout.Label(settings.privacyNotice, hintStyle);
            GUILayout.EndVertical();
            GUILayout.Space(18 * styleScale);
            GUILayout.BeginVertical(cardStyle, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            formScroll = GUILayout.BeginScrollView(formScroll, false, true, GUILayout.ExpandHeight(true));
            DrawForm(false);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.Space(28 * styleScale);
            GUILayout.EndHorizontal();
        }

        private void DrawCompactContent()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(28 * styleScale);
            GUILayout.BeginVertical(cardStyle, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            formScroll = GUILayout.BeginScrollView(formScroll, false, true, GUILayout.ExpandHeight(true));
            DrawScreenshotPanel((isAnnotatingScreenshot ? 260f : 160f) * styleScale);
            GUILayout.Space(14 * styleScale);
            DrawCaptureSummary();
            GUILayout.Space(18 * styleScale);
            DrawForm(true);
            GUILayout.Space(12 * styleScale);
            GUILayout.Label(settings.privacyNotice, hintStyle);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.Space(28 * styleScale);
            GUILayout.EndHorizontal();
        }

        private void DrawScreenshotPanel(float maximumHeight)
        {
            GUILayout.BeginHorizontal();
            DrawLabel("SCREENSHOT");
            GUILayout.FlexibleSpace();
            statusStyle.normal.textColor = new Color(0.49f, 0.83f, 1f);
            GUILayout.Label(screenshotBytes != null ? "READY" : "UNAVAILABLE", statusStyle);
            GUILayout.EndHorizontal();

            var aspect = screenshotPreview != null ? screenshotPreview.width / (float)Mathf.Max(1, screenshotPreview.height) : 16f / 9f;
            var previewHeight = Mathf.Min(maximumHeight, Mathf.Max(120f * styleScale, 390f * styleScale / aspect));
            var rect = GUILayoutUtility.GetRect(100f, previewHeight, GUILayout.ExpandWidth(true));
            GUI.DrawTexture(rect, fieldTexture);
            if (screenshotPreview != null)
            {
                var imageRect = new Rect(rect.x + 3, rect.y + 3, rect.width - 6, rect.height - 6);
                var fittedImageRect = FitTextureRect(imageRect, screenshotPreview.width, screenshotPreview.height);
                GUI.DrawTexture(fittedImageRect, screenshotPreview, ScaleMode.StretchToFill, false);
                if (isAnnotatingScreenshot && !isSending)
                    HandleScreenshotAnnotation(fittedImageRect);
            }
            else
            {
                GUI.Label(rect, "Screenshot unavailable", hintStyle);
            }

            GUILayout.Space(10 * styleScale);
            GUI.enabled = !isSending && !isOpening;
            if (!isAnnotatingScreenshot)
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = GUI.enabled && screenshotAnnotator != null;
                if (GUILayout.Button("ANNOTATE", primaryButtonStyle, GUILayout.Height(48 * styleScale)))
                    isAnnotatingScreenshot = true;
                GUI.enabled = !isSending && !isOpening;
                GUILayout.Space(10 * styleScale);
                if (GUILayout.Button("RECAPTURE", buttonStyle, GUILayout.Height(48 * styleScale)))
                    StartCoroutine(RecaptureScreenshot());
                GUILayout.EndHorizontal();
            }
            else
            {
                DrawAnnotationToolbar();
            }
            GUI.enabled = true;
        }

        private void DrawAnnotationToolbar()
        {
            var compact = windowRect.width < 900f;
            if (compact)
            {
                GUILayout.Label("DRAW ON THE SCREENSHOT", labelStyle);
                GUILayout.Space(6 * styleScale);
            }
            GUILayout.BeginHorizontal();
            if (!compact)
            {
                GUILayout.Label("DRAW ON THE SCREENSHOT", labelStyle);
                GUILayout.FlexibleSpace();
            }
            GUI.enabled = screenshotAnnotator != null && screenshotAnnotator.CanUndo;
            if (GUILayout.Button("UNDO", buttonStyle, compact ? GUILayout.ExpandWidth(true) : GUILayout.Width(96 * styleScale), GUILayout.Height(44 * styleScale)))
                screenshotAnnotator.Undo();
            GUI.enabled = screenshotAnnotator != null && screenshotAnnotator.HasAnnotations;
            if (GUILayout.Button("CLEAR", buttonStyle, compact ? GUILayout.ExpandWidth(true) : GUILayout.Width(96 * styleScale), GUILayout.Height(44 * styleScale)))
                screenshotAnnotator.Clear();
            GUI.enabled = true;
            if (GUILayout.Button("DONE", primaryButtonStyle, compact ? GUILayout.ExpandWidth(true) : GUILayout.Width(96 * styleScale), GUILayout.Height(44 * styleScale)))
            {
                screenshotAnnotator?.EndStroke();
                isAnnotatingScreenshot = false;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8 * styleScale);

            if (compact)
            {
                annotationColorIndex = GUILayout.SelectionGrid(annotationColorIndex, AnnotationColorLabels, 3, categoryStyle, GUILayout.Height(44 * styleScale));
                GUILayout.Space(8 * styleScale);
                annotationSizeIndex = GUILayout.SelectionGrid(annotationSizeIndex, AnnotationSizeLabels, 3, categoryStyle, GUILayout.Height(44 * styleScale));
            }
            else
            {
                GUILayout.BeginHorizontal();
                annotationColorIndex = GUILayout.SelectionGrid(annotationColorIndex, AnnotationColorLabels, 3, categoryStyle, GUILayout.Height(44 * styleScale));
                GUILayout.Space(10 * styleScale);
                annotationSizeIndex = GUILayout.SelectionGrid(annotationSizeIndex, AnnotationSizeLabels, 3, categoryStyle, GUILayout.Width(210 * styleScale), GUILayout.Height(44 * styleScale));
                GUILayout.EndHorizontal();
            }
        }

        private void HandleScreenshotAnnotation(Rect imageRect)
        {
            var current = Event.current;
            var controlId = GUIUtility.GetControlID("MacacaBeaconScreenshotAnnotation".GetHashCode(), FocusType.Passive, imageRect);
            if (current.type == EventType.MouseDown && current.button == 0 && imageRect.Contains(current.mousePosition))
            {
                GUIUtility.hotControl = controlId;
                screenshotAnnotator.BeginStroke(ToNormalizedPoint(imageRect, current.mousePosition), SelectedAnnotationColor(), SelectedBrushRadius());
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == controlId)
            {
                screenshotAnnotator.AddPoint(ToNormalizedPoint(imageRect, current.mousePosition));
                current.Use();
            }
            else if (current.type == EventType.MouseUp && GUIUtility.hotControl == controlId)
            {
                screenshotAnnotator.AddPoint(ToNormalizedPoint(imageRect, current.mousePosition));
                screenshotAnnotator.EndStroke();
                GUIUtility.hotControl = 0;
                current.Use();
            }
        }

        private Color32 SelectedAnnotationColor()
        {
            switch (annotationColorIndex)
            {
                case 1: return new Color32(255, 214, 64, 255);
                case 2: return new Color32(35, 215, 242, 255);
                default: return new Color32(255, 72, 82, 255);
            }
        }

        private int SelectedBrushRadius()
        {
            var shortEdge = Mathf.Min(screenshotAnnotator.Width, screenshotAnnotator.Height);
            var ratio = annotationSizeIndex == 0 ? 0.004f : annotationSizeIndex == 2 ? 0.014f : 0.008f;
            return Mathf.Max(2, Mathf.RoundToInt(shortEdge * ratio));
        }

        private static Vector2 ToNormalizedPoint(Rect rect, Vector2 point)
        {
            return new Vector2(
                Mathf.Clamp01((point.x - rect.x) / Mathf.Max(1f, rect.width)),
                Mathf.Clamp01((point.y - rect.y) / Mathf.Max(1f, rect.height)));
        }

        private static Rect FitTextureRect(Rect bounds, int textureWidth, int textureHeight)
        {
            var scale = Mathf.Min(bounds.width / Mathf.Max(1, textureWidth), bounds.height / Mathf.Max(1, textureHeight));
            var width = textureWidth * scale;
            var height = textureHeight * scale;
            return new Rect(bounds.x + (bounds.width - width) * 0.5f, bounds.y + (bounds.height - height) * 0.5f, width, height);
        }

        private void DrawCaptureSummary()
        {
            DrawLabel("REPORT CONTENTS");
            var screenshotState = settings.includeScreenshot && screenshotBytes != null
                ? screenshotAnnotator != null && screenshotAnnotator.HasAnnotations ? "[READY] Screenshot + annotations" : "[READY] Screenshot"
                : "[OFF] Screenshot";
            var videoState = !settings.enableRollingVideo ? "[OFF] Video" : videoRecorder.IsFinalizing ? "[WAIT] Video recording" : videoBytes != null ? "[READY] Video" : "[OFF] Video unavailable";
            var logState = settings.includeRecentLogs ? "[READY] Recent logs" : "[OFF] Recent logs";
            GUILayout.Label(screenshotState + "\n" + videoState + "\n" + logState, hintStyle);
        }

        private void DrawForm(bool compact)
        {
            DrawLabel("CATEGORY");
            var categories = SafeCategories();
            var rows = Mathf.CeilToInt(categories.Length / 3f);
            categoryIndex = GUILayout.SelectionGrid(categoryIndex, categories, 3, categoryStyle, GUILayout.Height(rows * 48f * styleScale));
            GUILayout.Space(14 * styleScale);

            DrawLabel("TITLE  *");
            GUI.SetNextControlName("BugReportTitle");
            title = GUILayout.TextField(title, 120, fieldStyle, GUILayout.Height(48 * styleScale));
            GUILayout.Space(12 * styleScale);

            DrawLabel("WHAT HAPPENED?  *");
            GUI.SetNextControlName("BugReportDescription");
            description = GUILayout.TextArea(description, 2000, areaStyle, GUILayout.MinHeight((compact ? 116 : 150) * styleScale));
            GUILayout.Space(12 * styleScale);

            DrawLabel("REPORTER / CONTACT  (OPTIONAL)");
            reporter = GUILayout.TextField(reporter, 120, fieldStyle, GUILayout.Height(48 * styleScale));

            if (!string.IsNullOrEmpty(validationMessage))
            {
                GUILayout.Space(10 * styleScale);
                GUILayout.Label(validationMessage, validationStyle);
            }
            if (!string.IsNullOrEmpty(status))
            {
                GUILayout.Space(10 * styleScale);
                statusStyle.normal.textColor = statusIsError ? new Color(1f, 0.55f, 0.55f) : new Color(0.49f, 0.83f, 1f);
                GUILayout.Label(status, statusStyle);
            }
        }

        private void TryBeginSend()
        {
            if (isSending || (settings.enableRollingVideo && videoRecorder.IsFinalizing))
                return;
            if (string.IsNullOrWhiteSpace(title))
            {
                validationMessage = "Add a short title so the team can scan this report.";
                pendingFocusControl = "BugReportTitle";
                return;
            }
            if (string.IsNullOrWhiteSpace(description))
            {
                validationMessage = "Describe what happened and what you expected instead.";
                pendingFocusControl = "BugReportDescription";
                return;
            }
            validationMessage = "";
            StartCoroutine(SendReport());
        }

        private IEnumerator RecaptureScreenshot()
        {
            isOpening = true;
            statusIsError = false;
            status = "Recapturing screenshot…";
            screenshotAnnotator = null;
            isAnnotatingScreenshot = false;
            IsOpen = false;
            yield return CaptureUtility.CapturePng((bytes, texture) =>
            {
                screenshotBytes = bytes;
                if (screenshotPreview != null) Destroy(screenshotPreview);
                screenshotPreview = texture;
                screenshotAnnotator = texture == null ? null : new ScreenshotAnnotator(texture);
            });
            IsOpen = true;
            isOpening = false;
            status = screenshotBytes != null ? "Screenshot updated." : "Could not capture screenshot.";
            statusIsError = screenshotBytes == null;
        }

        private IEnumerator SendReport()
        {
            isSending = true;
            statusIsError = false;
            status = "Sending report…";
            var report = BuildReport();
            string localArchivePath = null;
            string localArchiveError = null;
            if (settings.saveFailedReportsLocally && !LocalReportArchive.TryStage(report, settings.maximumRetainedLocalReports, out localArchivePath, out localArchiveError))
                Debug.LogError("[Macaca Beacon] Could not stage local report backup: " + localArchiveError);
            var transport = BugReporter.TransportOverride ?? new SlackBugReportTransport(settings.botToken, settings.channelId);
            var result = BugReportSendResult.Fail("The report transport ended without returning a result.");
            yield return transport.Send(report, value => result = value);
            isSending = false;
            status = result.Message;
            statusIsError = !result.Success;
            if (!result.Success)
            {
                if (!string.IsNullOrEmpty(localArchivePath))
                {
                    LocalReportArchive.MarkFailed(localArchivePath, result.Message);
                    status += "\nSaved locally: " + localArchivePath;
                }
                else if (settings.saveFailedReportsLocally)
                {
                    status += "\nLocal backup also failed: " + localArchiveError;
                }
                Debug.LogError("[Macaca Beacon] " + result.Message);
            }
            if (result.Success)
            {
                LocalReportArchive.Discard(localArchivePath);
                yield return new WaitForSecondsRealtime(1.2f);
                Close();
                title = "";
                description = "";
                validationMessage = "";
            }
        }

        private BugReport BuildReport()
        {
            var report = new BugReport
            {
                Id = Guid.NewGuid().ToString("N").Substring(0, 10).ToUpperInvariant(),
                CreatedUtc = DateTime.UtcNow,
                Reporter = reporter.Trim(),
                Category = SafeCategories()[Mathf.Clamp(categoryIndex, 0, SafeCategories().Length - 1)],
                Title = title.Trim(),
                Description = description.Trim()
            };
            report.Fields["Build"] = Application.version + " (" + Application.buildGUID + ")";
            report.Fields["Scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            report.Fields["UTC"] = report.CreatedUtc.ToString("O");

            if (settings.includeScreenshot && screenshotBytes != null)
            {
                var finalScreenshot = screenshotAnnotator != null && screenshotAnnotator.HasAnnotations
                    ? screenshotAnnotator.EncodePng()
                    : screenshotBytes;
                AddAttachmentIfAllowed(report, new BugReportAttachment("bug-" + report.Id + ".png", "image/png", finalScreenshot, "Game screenshot at report time with optional annotations"));
            }
            if (settings.enableRollingVideo && videoBytes != null)
                AddAttachmentIfAllowed(report, new BugReportAttachment("bug-" + report.Id + ".avi", "video/x-msvideo", videoBytes, "Gameplay around report time"));
            if (settings.includeDiagnostics || settings.includeRecentLogs)
            {
                var diagnostics = BuildDiagnostics();
                AddAttachmentIfAllowed(report, new BugReportAttachment("diagnostics-" + report.Id + ".txt", "text/plain", Encoding.UTF8.GetBytes(diagnostics), "Device diagnostics and recent logs"));
            }
            BugReporter.CollectCustomData(report);
            return report;
        }

        private void AddAttachmentIfAllowed(BugReport report, BugReportAttachment attachment)
        {
            var maximumBytes = settings.maximumAttachmentMegabytes * 1024L * 1024L;
            if (attachment.Data != null && attachment.Data.LongLength <= maximumBytes)
                report.Attachments.Add(attachment);
            else
                Debug.LogWarning("Macaca Beacon skipped oversized attachment: " + attachment.FileName);
        }

        private string BuildDiagnostics()
        {
            var builder = new StringBuilder();
            if (settings.includeDiagnostics)
            {
                builder.AppendLine("Macaca Beacon diagnostics");
                builder.AppendLine("UTC: " + DateTime.UtcNow.ToString("O"));
                builder.AppendLine("Product: " + Application.productName);
                builder.AppendLine("Version: " + Application.version);
                builder.AppendLine("Build GUID: " + Application.buildGUID);
                builder.AppendLine("Unity: " + Application.unityVersion);
                builder.AppendLine("Platform: " + Application.platform);
                builder.AppendLine("OS: " + SystemInfo.operatingSystem);
                builder.AppendLine("CPU: " + SystemInfo.processorType + " (" + SystemInfo.processorCount + " cores)");
                builder.AppendLine("RAM: " + SystemInfo.systemMemorySize + " MB");
                builder.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " / " + SystemInfo.graphicsDeviceVersion);
                builder.AppendLine("VRAM: " + SystemInfo.graphicsMemorySize + " MB");
                builder.AppendLine("Resolution: " + Screen.width + "x" + Screen.height + " @ " + Screen.currentResolution.refreshRateRatio.value.ToString("0.##") + " Hz");
                builder.AppendLine("Scene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            }
            if (settings.includeRecentLogs)
            {
                builder.AppendLine();
                builder.AppendLine("Recent logs");
                builder.AppendLine("-----------");
                builder.Append(logs.BuildText());
            }
            return builder.ToString();
        }

        private string[] SafeCategories()
        {
            return settings.categories != null && settings.categories.Length > 0 ? settings.categories : new[] { "Other" };
        }

        private void DrawLabel(string value) => GUILayout.Label(value, labelStyle);

        private void EnsureStyles(float scale)
        {
            if (titleStyle != null && Mathf.Abs(styleScale - scale) < 0.01f)
                return;

            ReleaseStyleTextures();
            styleScale = scale;
            var window = MakeTexture(new Color(0.075f, 0.102f, 0.145f, 1f));
            var card = MakeTexture(new Color(0.105f, 0.145f, 0.205f, 1f));
            var field = MakeTexture(new Color(0.045f, 0.075f, 0.115f, 1f));
            var fieldHover = MakeTexture(new Color(0.065f, 0.115f, 0.165f, 1f));
            var fieldFocus = MakeTexture(new Color(0.075f, 0.18f, 0.27f, 1f));
            var secondary = MakeTexture(new Color(0.14f, 0.20f, 0.29f, 1f));
            var secondaryHover = MakeTexture(new Color(0.19f, 0.29f, 0.41f, 1f));
            var secondaryActive = MakeTexture(new Color(0.09f, 0.15f, 0.22f, 1f));
            var selected = MakeTexture(new Color(0.02f, 0.40f, 0.55f, 1f));
            var selectedHover = MakeTexture(new Color(0.03f, 0.50f, 0.66f, 1f));
            var accent = MakeTexture(new Color(0.22f, 0.74f, 0.97f, 1f));
            var accentHover = MakeTexture(new Color(0.49f, 0.83f, 1f, 1f));
            var accentActive = MakeTexture(new Color(0.12f, 0.58f, 0.78f, 1f));

            windowTexture = window;
            accentTexture = accent;
            fieldTexture = field;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(26 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 0, 0)
            };
            titleStyle.normal.textColor = new Color(0.97f, 0.98f, 1f);

            subtitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13 * scale),
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 2, 0)
            };
            subtitleStyle.normal.textColor = new Color(0.68f, 0.76f, 0.86f);

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(12 * scale),
                fontStyle = FontStyle.Bold,
                margin = new RectOffset(0, 0, Mathf.RoundToInt(3 * scale), Mathf.RoundToInt(7 * scale))
            };
            labelStyle.normal.textColor = new Color(0.78f, 0.85f, 0.94f);

            hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(13 * scale),
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft
            };
            hintStyle.normal.textColor = new Color(0.69f, 0.76f, 0.85f);

            cardStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = card },
                padding = new RectOffset(Mathf.RoundToInt(20 * scale), Mathf.RoundToInt(20 * scale), Mathf.RoundToInt(18 * scale), Mathf.RoundToInt(18 * scale)),
                margin = new RectOffset(0, 0, 0, 0)
            };

            fieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = Mathf.RoundToInt(16 * scale),
                padding = new RectOffset(Mathf.RoundToInt(14 * scale), Mathf.RoundToInt(14 * scale), Mathf.RoundToInt(12 * scale), Mathf.RoundToInt(10 * scale))
            };
            fieldStyle.normal.background = field;
            fieldStyle.hover.background = fieldHover;
            fieldStyle.focused.background = fieldFocus;
            fieldStyle.normal.textColor = fieldStyle.hover.textColor = fieldStyle.focused.textColor = Color.white;

            areaStyle = new GUIStyle(fieldStyle) { wordWrap = true, alignment = TextAnchor.UpperLeft };

            buttonStyle = CreateButtonStyle(scale, secondary, secondaryHover, secondaryActive, Color.white);
            categoryStyle = CreateButtonStyle(scale, secondary, secondaryHover, secondaryActive, Color.white);
            categoryStyle.onNormal.background = selected;
            categoryStyle.onHover.background = selectedHover;
            categoryStyle.onActive.background = selectedHover;
            categoryStyle.onNormal.textColor = categoryStyle.onHover.textColor = categoryStyle.onActive.textColor = Color.white;
            categoryStyle.margin = new RectOffset(4, 4, 4, 4);

            primaryButtonStyle = CreateButtonStyle(scale, accent, accentHover, accentActive, new Color(0.02f, 0.08f, 0.12f));
            primaryButtonStyle.fontSize = Mathf.RoundToInt(14 * scale);
            closeButtonStyle = new GUIStyle(buttonStyle) { fontSize = Mathf.RoundToInt(12 * scale) };

            statusStyle = new GUIStyle(hintStyle) { fontStyle = FontStyle.Bold };
            statusStyle.normal.textColor = new Color(0.49f, 0.83f, 1f);
            validationStyle = new GUIStyle(hintStyle) { fontStyle = FontStyle.Bold };
            validationStyle.normal.textColor = new Color(1f, 0.61f, 0.61f);
        }

        private GUIStyle CreateButtonStyle(float scale, Texture2D normal, Texture2D hover, Texture2D active, Color text)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(13 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(Mathf.RoundToInt(12 * scale), Mathf.RoundToInt(12 * scale), Mathf.RoundToInt(10 * scale), Mathf.RoundToInt(10 * scale)),
                margin = new RectOffset(4, 4, 4, 4)
            };
            style.normal.background = normal;
            style.hover.background = hover;
            style.active.background = active;
            style.focused.background = hover;
            style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = text;
            return style;
        }

        private Texture2D MakeTexture(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            texture.SetPixel(0, 0, color);
            texture.Apply(false, true);
            styleTextures.Add(texture);
            return texture;
        }

        private void ReleaseStyleTextures()
        {
            foreach (var texture in styleTextures)
            {
                if (texture != null)
                    Destroy(texture);
            }
            styleTextures.Clear();
            titleStyle = null;
        }
    }
}
