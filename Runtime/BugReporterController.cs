using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed class BugReporterController : MonoBehaviour
    {
        private static readonly string[] AnnotationColorLabels = { "RED", "YELLOW", "CYAN" };
        private static readonly string[] AnnotationSizeLabels = { "S", "M", "L" };

        internal static BugReporterController Instance { get; private set; }
        internal bool IsOpen { get; private set; }
        internal bool IsVideoRecordingEnabled => videoRecorder != null && videoRecorder.IsEnabled;

        private BugReporterSettings settings;
        private RecentLogCollector logs;
        private RollingVideoRecorder videoRecorder;
        private byte[] screenshotBytes;
        private VideoCaptureResult videoCapture;
        private Texture2D screenshotPreview;
        private ScreenshotAnnotator screenshotAnnotator;
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
        private Vector2 contentScroll;
        private int touchScrollId = -1;
        private int touchScrollTarget;
        private Vector2 lastTouchScrollPosition;
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
        private GUIStyle entryButtonStyle;
#if UNITY_IOS || UNITY_ANDROID
        private float mobileGestureStartedAt = -1f;
        private bool mobileGestureTriggered;
#endif
        private GameObject inputBlocker;

        // The two-column layout needs more room than its old 900 px cutoff
        // suggests: the screenshot card, form card, gutters, and card padding
        // all have useful minimum widths. Keep portrait and split-screen views
        // stacked so the form never collapses into a clipped sliver.
        private const float DesktopLayoutMinimumWidth = 1120f;
        private const float MobileLayoutMaximumWidth = 720f;

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
            SetInputBlocker(false);
            videoCapture?.DeleteFile();
            videoCapture = null;
            Cursor.lockState = previousCursorLock;
            Cursor.visible = previousCursorVisible;
            status = "";
        }

        internal void SetVideoRecordingEnabled(bool enabled)
        {
            videoRecorder?.SetEnabled(enabled);
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
            videoRecorder?.Dispose();
            if (inputBlocker != null)
                Destroy(inputBlocker);
            if (screenshotPreview != null) Destroy(screenshotPreview);
            ReleaseStyleTextures();
        }

        private void Update()
        {
            if (IsOpen && !isSending && videoCapture == null && videoRecorder != null && videoRecorder.IsEncoding)
            {
                statusIsError = false;
                status = "Encoding video in the background — you can keep typing.";
            }
#if UNITY_IOS || UNITY_ANDROID
            if (settings == null || IsOpen || isOpening || !settings.enableThreeFingerGesture)
            {
                mobileGestureStartedAt = -1f;
                mobileGestureTriggered = false;
                return;
            }

            if (Input.touchCount < 3)
            {
                mobileGestureStartedAt = -1f;
                mobileGestureTriggered = false;
                return;
            }

            if (mobileGestureStartedAt < 0f)
                mobileGestureStartedAt = Time.unscaledTime;
            if (!mobileGestureTriggered && Time.unscaledTime - mobileGestureStartedAt >= settings.threeFingerGestureHoldSeconds)
            {
                mobileGestureTriggered = true;
                RequestOpen();
            }
#endif
        }

        private void SetInputBlocker(bool blocked)
        {
            if (inputBlocker == null && blocked)
            {
                inputBlocker = new GameObject("Macaca Beacon Input Blocker");
                inputBlocker.transform.SetParent(transform, false);

                var canvas = inputBlocker.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = short.MaxValue;
                inputBlocker.AddComponent<GraphicRaycaster>();

                var image = inputBlocker.AddComponent<Image>();
                image.color = Color.clear;
                image.raycastTarget = true;

                var rect = inputBlocker.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            if (inputBlocker != null)
                inputBlocker.SetActive(blocked);
        }

        private IEnumerator OpenAfterCapture()
        {
            isOpening = true;
            status = "Capturing context…";
            screenshotBytes = null;
            videoCapture?.DeleteFile();
            videoCapture = null;
            screenshotAnnotator = null;
            videoRecorder.MarkIncident(result =>
            {
                if (!IsOpen && !isOpening)
                {
                    result?.DeleteFile();
                    return;
                }
                videoCapture = result;
                if (IsOpen)
                {
                    statusIsError = result == null;
                    status = result == null
                        ? "Video could not be finalized. The report can still be sent without it."
                        : result.Extension.TrimStart('.').ToUpperInvariant() + " video ready (" + result.DurationSeconds.ToString("0.0") + "s).";
                }
            });

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
            SetInputBlocker(true);
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
            if (!IsOpen && !isOpening && settings.showEntryButton)
                DrawEntryButton();
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

            var uiScale = GetUiScale();
            EnsureStyles(uiScale);
            var overlay = new Rect(0, 0, Screen.width, Screen.height);

            GUI.depth = -10000;
            GUI.color = new Color(0.247f, 0.227f, 0.196f, settings.backdropOpacity);
            GUI.DrawTexture(overlay, Texture2D.whiteTexture);
            GUI.color = Color.white;

            float width;
            float height;
            if (settings.fullscreen)
            {
                windowRect = GetSafeAreaGuiRect();
                width = windowRect.width;
                height = windowRect.height;
            }
            else
            {
                var outerMargin = Mathf.Clamp(Mathf.Min(Screen.width, Screen.height) * 0.035f, 20f, 48f);
                width = Mathf.Min(Mathf.Clamp(Screen.width * settings.desktopWidthRatio, 760f, 1180f), Screen.width - outerMargin * 2f);
                height = Mathf.Min(Mathf.Clamp(Screen.height * 0.88f, 620f, 880f), Screen.height - outerMargin * 2f);
                windowRect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
            }

            // Fill only the area outside the safe-area window. Drawing four
            // non-overlapping strips avoids a full-screen texture covering
            // the IMGUI window on device simulators with different GUI depth
            // ordering.
            if (settings.fullscreen)
            {
                GUI.color = Color.white;
                var safeTop = windowRect.y;
                var safeBottom = windowRect.yMax;
                var safeLeft = windowRect.x;
                var safeRight = windowRect.xMax;
                if (safeTop > 0f)
                    GUI.DrawTexture(new Rect(0f, 0f, Screen.width, safeTop), windowTexture);
                if (safeBottom < Screen.height)
                    GUI.DrawTexture(new Rect(0f, safeBottom, Screen.width, Screen.height - safeBottom), windowTexture);
                if (safeLeft > 0f)
                    GUI.DrawTexture(new Rect(0f, safeTop, safeLeft, windowRect.height), windowTexture);
                if (safeRight < Screen.width)
                    GUI.DrawTexture(new Rect(safeRight, safeTop, Screen.width - safeRight, windowRect.height), windowTexture);
            }
            HandleScrollInput();
            GUI.color = Color.white;
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
            if (!IsMobileLayout())
                GUILayout.Label(settings.shortcut + "  toggle", hintStyle, GUILayout.Width(88 * styleScale));
            GUI.enabled = !isSending;
            if (GUILayout.Button("CLOSE", closeButtonStyle, GUILayout.Width(82 * styleScale), GUILayout.Height(44 * styleScale)))
                Close();
            GUI.enabled = true;
            GUILayout.Space(28 * styleScale);
            GUILayout.EndHorizontal();
            GUILayout.Space(18 * styleScale);

            var contentTop = 94f * styleScale;
            var footerHeight = 86f * styleScale;
            var contentHeight = Mathf.Max(220f, windowRect.height - contentTop - footerHeight);
            GUILayout.BeginArea(new Rect(0f, contentTop, windowRect.width, contentHeight));
            if (CanUseDesktopLayout())
            {
                DrawDesktopContent();
            }
            else
                DrawCompactContent();
            GUILayout.EndArea();

            GUILayout.BeginArea(new Rect(0f, windowRect.height - footerHeight, windowRect.width, footerHeight));
            GUILayout.BeginHorizontal();
            var footerPadding = IsMobileLayout() ? 12f * styleScale : 28f * styleScale;
            var videoPending = settings.enableRollingVideo && videoRecorder.IsFinalizing;
            var canSend = !isSending && !videoPending;
            var sendLabel = isSending
                ? "SENDING…"
                : videoRecorder.IsEncoding
                    ? "ENCODING VIDEO…"
                    : videoPending
                        ? "RECORDING VIDEO…"
                        : "SEND TO SLACK";
            GUILayout.Space(footerPadding);
            GUI.enabled = !isSending;
            if (IsMobileLayout())
            {
                var mobileButtonWidth = (windowRect.width - footerPadding * 2f - 8f * styleScale) * 0.5f;
                if (GUILayout.Button("CANCEL", buttonStyle, GUILayout.Height(48 * styleScale), GUILayout.Width(mobileButtonWidth)))
                    Close();
                GUI.enabled = canSend;
                if (GUILayout.Button(sendLabel, primaryButtonStyle, GUILayout.Height(48 * styleScale), GUILayout.Width(mobileButtonWidth)))
                    TryBeginSend();
            }
            else
            {
                if (GUILayout.Button("CANCEL", buttonStyle, GUILayout.Height(48 * styleScale), GUILayout.Width(120 * styleScale)))
                    Close();
                GUI.enabled = true;
                GUILayout.FlexibleSpace();
                GUILayout.Label("Ctrl / Cmd + Enter", hintStyle, GUILayout.Width(142 * styleScale));
                GUI.enabled = canSend;
                if (GUILayout.Button(sendLabel, primaryButtonStyle, GUILayout.Height(48 * styleScale), GUILayout.Width(190 * styleScale)))
                    TryBeginSend();
            }
            GUI.enabled = true;
            GUILayout.Space(footerPadding);
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUILayout.EndVertical();
        }

        private void DrawDesktopContent()
        {
            var availableWidth = windowRect.width - 56f * styleScale;
            // Keep the capture tools useful while giving the form more room
            // for titles, descriptions, and category controls.
            var leftWidth = availableWidth * 0.44f;
            var contentHeight = GetContentHeight();
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            GUILayout.Space(28 * styleScale);
            GUILayout.BeginVertical(cardStyle, GUILayout.Width(leftWidth), GUILayout.ExpandHeight(true));
            contentScroll = GUILayout.BeginScrollView(contentScroll, false, true, GUILayout.ExpandHeight(true));
            var previewWidth = Mathf.Max(100f,
                leftWidth - cardStyle.padding.left - cardStyle.padding.right - 24f * styleScale);
            DrawScreenshotPanel(previewWidth, Mathf.Clamp(contentHeight * 0.36f, 220f, 480f));
            GUILayout.Space(16 * styleScale);
            DrawCaptureSummary();
            GUILayout.FlexibleSpace();
            GUILayout.Label(settings.privacyNotice, hintStyle);
            GUILayout.EndScrollView();
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

        private bool CanUseDesktopLayout()
        {
            // A large interface scale consumes the same physical space as a
            // smaller window. Account for it before choosing two columns;
            // otherwise common 1366x768 displays get clipped scroll views.
            var scale = Mathf.Max(1f, styleScale);
            return windowRect.width >= DesktopLayoutMinimumWidth * scale
                && windowRect.height >= 720f * scale;
        }

        private bool IsMobileLayout()
        {
            return windowRect.width <= MobileLayoutMaximumWidth;
        }

        private float GetUiScale()
        {
            var viewportScale = Mathf.Min(Screen.width / 1280f, Screen.height / 900f);
            if (Screen.width <= MobileLayoutMaximumWidth)
            {
                // Mobile GUI text should follow readable touch targets rather
                // than shrinking with the raw pixel resolution.
                var mobileScale = Mathf.Clamp(Mathf.Min(Screen.width / 420f, Screen.height / 740f), 0.98f, 1.12f);
                return settings.interfaceScale * mobileScale;
            }
            return Mathf.Clamp(viewportScale, 0.9f, 1.25f) * settings.interfaceScale;
        }

        private float GetContentHeight()
        {
            return Mathf.Max(220f, windowRect.height - 94f * styleScale - 86f * styleScale);
        }

        internal static Rect ToGuiSafeArea(Rect safeArea, float screenHeight)
        {
            return new Rect(safeArea.x, screenHeight - safeArea.yMax, safeArea.width, safeArea.height);
        }

        internal static float SelectEntryButtonBaseSize(bool mobile, float desktopSize, float mobileSize)
        {
            return mobile ? mobileSize : desktopSize;
        }

        internal static Rect GetEntryButtonRect(Rect safeArea, float size, float margin, EntryButtonCorner corner)
        {
            var left = corner == EntryButtonCorner.TopLeft || corner == EntryButtonCorner.BottomLeft;
            var top = corner == EntryButtonCorner.TopLeft || corner == EntryButtonCorner.TopRight;
            return new Rect(
                left ? safeArea.xMin + margin : safeArea.xMax - size - margin,
                top ? safeArea.yMin + margin : safeArea.yMax - size - margin,
                size,
                size);
        }

        private Rect GetSafeAreaGuiRect()
        {
            var safeArea = ToGuiSafeArea(Screen.safeArea, Screen.height);
            var inset = Screen.width <= MobileLayoutMaximumWidth ? 8f : 0f;
            return new Rect(
                safeArea.xMin + inset,
                safeArea.yMin + inset,
                Mathf.Max(1f, safeArea.width - inset * 2f),
                Mathf.Max(1f, safeArea.height - inset * 2f));
        }

        private void HandleScrollInput()
        {
            var current = Event.current;
            if (current.type == EventType.ScrollWheel)
            {
                var target = GetScrollTarget(current.mousePosition);
                if (target == 1)
                    contentScroll.y = Mathf.Max(0f, contentScroll.y + current.delta.y * 24f);
                else if (target == 2)
                    formScroll.y = Mathf.Max(0f, formScroll.y + current.delta.y * 24f);
                current.Use();
            }

            // OnGUI is evaluated multiple times per frame. Process touch
            // deltas once during Repaint so one swipe is not applied more
            // than once by the layout and input passes.
            if (current.type != EventType.Repaint)
                return;

            if (Input.touchCount == 0)
            {
                touchScrollId = -1;
                return;
            }

            Touch touch = default(Touch);
            for (var i = 0; i < Input.touchCount; i++)
            {
                var candidate = Input.GetTouch(i);
                if (touchScrollId < 0 && candidate.phase == TouchPhase.Began)
                {
                    var point = new Vector2(candidate.position.x, Screen.height - candidate.position.y);
                    if (windowRect.Contains(point))
                    {
                        touchScrollId = candidate.fingerId;
                        touchScrollTarget = GetScrollTarget(point);
                        lastTouchScrollPosition = point;
                    }
                }
                if (candidate.fingerId == touchScrollId)
                    touch = candidate;
            }

            if (touchScrollId < 0)
                return;

            var touchPoint = new Vector2(touch.position.x, Screen.height - touch.position.y);
            if (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary)
            {
                var deltaY = touchPoint.y - lastTouchScrollPosition.y;
                if (touchScrollTarget == 1)
                    contentScroll.y = Mathf.Max(0f, contentScroll.y - deltaY);
                else if (touchScrollTarget == 2)
                    formScroll.y = Mathf.Max(0f, formScroll.y - deltaY);
                lastTouchScrollPosition = touchPoint;
            }
            if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                touchScrollId = -1;
        }

        private int GetScrollTarget(Vector2 point)
        {
            if (!windowRect.Contains(point))
                return 0;
            if (!CanUseDesktopLayout())
                return 2;

            var leftWidth = (windowRect.width - 56f * styleScale) * 0.44f;
            var leftEdge = windowRect.x + 28f * styleScale;
            return point.x < leftEdge + leftWidth ? 1 : 2;
        }

        private void DrawCompactContent()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(28 * styleScale);
            GUILayout.BeginVertical(cardStyle, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            formScroll = GUILayout.BeginScrollView(formScroll, false, true, GUILayout.ExpandHeight(true));
            var previewWidth = Mathf.Max(100f,
                windowRect.width - 56f * styleScale - cardStyle.padding.left - cardStyle.padding.right - 24f * styleScale);
            DrawScreenshotPanel(previewWidth, Mathf.Clamp(windowRect.width * 0.58f, 200f, 460f));
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

        private void DrawScreenshotPanel(float previewWidth, float fallbackHeight)
        {
            GUILayout.BeginHorizontal();
            DrawLabel("SCREENSHOT");
            GUILayout.FlexibleSpace();
            statusStyle.normal.textColor = new Color(0.09f, 0.48f, 0.50f);
            GUILayout.Label(screenshotBytes != null ? "READY" : "UNAVAILABLE", statusStyle);
            GUILayout.EndHorizontal();

            var previewHeight = fallbackHeight;
            if (screenshotPreview != null && screenshotPreview.width > 0 && screenshotPreview.height > 0)
            {
                // Match the preview frame to the captured image so the dark
                // texture does not create large letterboxed bands around it.
                previewHeight = previewWidth * screenshotPreview.height / screenshotPreview.width;
            }
            previewHeight = Mathf.Max(160f * styleScale, previewHeight);
            // Keep the preview frame at the captured image size. Expanding
            // this rect to the whole card creates dark side bands around the
            // screenshot even when its aspect ratio is already correct.
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var rect = GUILayoutUtility.GetRect(
                previewWidth,
                previewHeight,
                GUILayout.Width(previewWidth),
                GUILayout.Height(previewHeight));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUI.DrawTexture(rect, fieldTexture);
            if (screenshotPreview != null)
            {
                var imageRect = new Rect(rect.x + 3, rect.y + 3, rect.width - 6, rect.height - 6);
                var fittedImageRect = FitTextureRect(imageRect, screenshotPreview.width, screenshotPreview.height);
                GUI.DrawTexture(fittedImageRect, screenshotPreview, ScaleMode.StretchToFill, false);
                if (!isSending && screenshotAnnotator != null)
                    HandleScreenshotAnnotation(fittedImageRect);
            }
            else
            {
                GUI.Label(rect, "Screenshot unavailable", hintStyle);
            }

            GUILayout.Space(10 * styleScale);
            DrawAnnotationToolbar(!isSending && !isOpening && screenshotAnnotator != null);
            GUI.enabled = true;
        }

        private void DrawAnnotationToolbar(bool canInteract)
        {
            var compact = !CanUseDesktopLayout();
            if (compact)
            {
                GUILayout.Label("DRAW ON THE SCREENSHOT", labelStyle);
                GUILayout.Space(6 * styleScale);
            }
            if (compact)
            {
                GUILayout.BeginHorizontal();
                DrawAnnotationButton("UNDO", canInteract && screenshotAnnotator.CanUndo, () => screenshotAnnotator.Undo());
                DrawAnnotationButton("CLEAR", canInteract && screenshotAnnotator.HasAnnotations, () => screenshotAnnotator.Clear());
                GUILayout.EndHorizontal();
                DrawAnnotationButton("RECAPTURE SCREENSHOT", canInteract, () => StartCoroutine(RecaptureScreenshot()));
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("DRAW ON THE SCREENSHOT", labelStyle);
                GUILayout.FlexibleSpace();
                DrawAnnotationButton("UNDO", canInteract && screenshotAnnotator.CanUndo, () => screenshotAnnotator.Undo(), 96 * styleScale);
                DrawAnnotationButton("CLEAR", canInteract && screenshotAnnotator.HasAnnotations, () => screenshotAnnotator.Clear(), 96 * styleScale);
                DrawAnnotationButton("RECAPTURE", canInteract, () => StartCoroutine(RecaptureScreenshot()), 150 * styleScale);
                GUILayout.EndHorizontal();
            }
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

        private void DrawAnnotationButton(string label, bool enabled, Action action, float width = -1f)
        {
            GUI.enabled = enabled;
            var clicked = width > 0f
                ? GUILayout.Button(label, buttonStyle, GUILayout.Width(width), GUILayout.Height(44 * styleScale))
                : GUILayout.Button(label, buttonStyle, GUILayout.ExpandWidth(true), GUILayout.Height(44 * styleScale));
            if (clicked)
                action();
            GUI.enabled = true;
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
            var videoState = !settings.enableRollingVideo
                ? "[OFF] Video"
                : videoRecorder.IsEncoding
                    ? "[WAIT] Video encoding in background"
                    : videoRecorder.IsFinalizing
                        ? "[WAIT] Video recording"
                        : videoCapture != null
                            ? "[READY] " + videoCapture.Extension.TrimStart('.').ToUpperInvariant() + " video"
                            : "[OFF] Video unavailable";
            var logState = settings.includeRecentLogs ? "[READY] Recent logs" : "[OFF] Recent logs";
            GUILayout.Label(screenshotState + "\n" + videoState + "\n" + logState, hintStyle);
        }

        private void DrawForm(bool compact)
        {
            DrawLabel("CATEGORY");
            var categories = SafeCategories();
            var columns = compact && windowRect.width < 560f ? 2 : 3;
            var rows = Mathf.CeilToInt(categories.Length / (float)columns);
            categoryIndex = GUILayout.SelectionGrid(categoryIndex, categories, columns, categoryStyle, GUILayout.Height(rows * 48f * styleScale));
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
                statusStyle.normal.textColor = statusIsError ? new Color(0.70f, 0.23f, 0.20f) : new Color(0.09f, 0.48f, 0.50f);
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
            IsOpen = false;
            SetInputBlocker(false);
            yield return CaptureUtility.CapturePng((bytes, texture) =>
            {
                screenshotBytes = bytes;
                if (screenshotPreview != null) Destroy(screenshotPreview);
                screenshotPreview = texture;
                screenshotAnnotator = texture == null ? null : new ScreenshotAnnotator(texture);
            });
            IsOpen = true;
            SetInputBlocker(true);
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
            report.Fields["Build"] = BuildVersionLabel();
            report.Fields["Scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            report.Fields["UTC"] = report.CreatedUtc.ToString("O");
            report.Fields["Device Model"] = SystemInfo.deviceModel;
            report.Fields["CPU"] = SystemInfo.processorType + " (" + SystemInfo.processorCount + " cores)";
            report.Fields["RAM"] = SystemInfo.systemMemorySize + " MB";
            report.Fields["OS"] = SystemInfo.operatingSystem;
            report.Fields["GPU"] = SystemInfo.graphicsDeviceName;

            if (settings.includeScreenshot && screenshotBytes != null)
            {
                var finalScreenshot = screenshotAnnotator != null && screenshotAnnotator.HasAnnotations
                    ? screenshotAnnotator.EncodePng()
                    : screenshotBytes;
                AddAttachmentIfAllowed(report, new BugReportAttachment("bug-" + report.Id + ".png", "image/png", finalScreenshot, "Game screenshot at report time with optional annotations"));
            }
            if (videoCapture != null)
            {
                var videoAttachment = BugReportAttachment.FromFile(
                    "bug-" + report.Id + videoCapture.Extension,
                    videoCapture.MimeType,
                    videoCapture.FilePath,
                    "Gameplay around report time");
                videoAttachment.DeleteSourceAfterStaging = true;
                AddAttachmentIfAllowed(report, videoAttachment);
            }
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
            if (attachment.Length > 0 && attachment.Length <= maximumBytes)
                report.Attachments.Add(attachment);
            else
            {
                Debug.LogWarning("Macaca Beacon skipped oversized attachment: " + attachment.FileName);
                if (attachment.DeleteSourceAfterStaging && !string.IsNullOrEmpty(attachment.FilePath))
                {
                    try { System.IO.File.Delete(attachment.FilePath); }
                    catch (Exception exception) { Debug.LogWarning("Macaca Beacon could not clean up skipped attachment: " + exception.Message); }
                }
            }
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
                builder.AppendLine("Build: " + BuildVersionLabel());
                builder.AppendLine("Unity: " + Application.unityVersion);
                builder.AppendLine("Platform: " + Application.platform);
                builder.AppendLine("OS: " + SystemInfo.operatingSystem);
                builder.AppendLine("Device Model: " + SystemInfo.deviceModel);
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

        private static string BuildVersionLabel()
        {
            var version = string.IsNullOrEmpty(Application.version) ? "N/A" : Application.version;
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconVideo"))
                {
                    var versionCode = bridge.CallStatic<int>("getVersionCode");
                    if (versionCode >= 0)
                        return version + " (Code: " + versionCode + ")";
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Macaca Beacon] Failed to read Android version code: " + exception.Message);
            }
#elif UNITY_IOS && !UNITY_EDITOR
            try
            {
                var buildNumber = MacacaBeaconVideo_GetBuildNumber();
                if (buildNumber >= 0)
                    return version + " (Build: " + buildNumber + ")";
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[Macaca Beacon] Failed to read iOS build number: " + exception.Message);
            }
#endif
            return version;
        }

#if UNITY_IOS && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern int MacacaBeaconVideo_GetBuildNumber();
#endif

        private void DrawLabel(string value) => GUILayout.Label(value, labelStyle);

        private void EnsureStyles(float scale)
        {
            if (titleStyle != null && Mathf.Abs(styleScale - scale) < 0.01f)
                return;

            ReleaseStyleTextures();
            styleScale = scale;
            // Macaca Games brand palette: warm paper, charcoal ink, monkey orange and a small cyan accent.
            var window = MakeTexture(new Color(0.980f, 0.965f, 0.925f, 1f));
            var card = MakeTexture(Color.white);
            var preview = MakeTexture(new Color(0.039f, 0.071f, 0.125f, 1f));
            var field = MakeTexture(new Color(0.949f, 0.922f, 0.859f, 1f));
            var fieldHover = MakeTexture(new Color(1f, 0.973f, 0.914f, 1f));
            var fieldFocus = MakeTexture(new Color(1f, 0.941f, 0.824f, 1f));
            var secondary = MakeTexture(new Color(0.941f, 0.914f, 0.855f, 1f));
            var secondaryHover = MakeTexture(new Color(0.902f, 0.863f, 0.796f, 1f));
            var secondaryActive = MakeTexture(new Color(0.867f, 0.820f, 0.745f, 1f));
            var selected = MakeTexture(new Color(1f, 0.686f, 0.094f, 1f));
            var selectedHover = MakeTexture(new Color(0.945f, 0.502f, 0.110f, 1f));
            var accent = MakeTexture(new Color(0.945f, 0.502f, 0.110f, 1f));
            var accentHover = MakeTexture(new Color(1f, 0.686f, 0.094f, 1f));
            var accentActive = MakeTexture(new Color(0.831f, 0.373f, 0.055f, 1f));

            windowTexture = window;
            accentTexture = accent;
            fieldTexture = preview;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(30 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 0, 0)
            };
            titleStyle.normal.textColor = new Color(0.247f, 0.227f, 0.196f);

            subtitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(15 * scale),
                alignment = TextAnchor.MiddleLeft,
                margin = new RectOffset(0, 0, 2, 0)
            };
            subtitleStyle.normal.textColor = new Color(0.42f, 0.39f, 0.34f);

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(14 * scale),
                fontStyle = FontStyle.Bold,
                margin = new RectOffset(0, 0, Mathf.RoundToInt(3 * scale), Mathf.RoundToInt(7 * scale))
            };
            labelStyle.normal.textColor = new Color(0.31f, 0.29f, 0.25f);

            hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(15 * scale),
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft
            };
            hintStyle.normal.textColor = new Color(0.43f, 0.40f, 0.35f);

            cardStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = card },
                padding = new RectOffset(Mathf.RoundToInt(20 * scale), Mathf.RoundToInt(20 * scale), Mathf.RoundToInt(18 * scale), Mathf.RoundToInt(18 * scale)),
                margin = new RectOffset(0, 0, 0, 0)
            };

            fieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = Mathf.RoundToInt(18 * scale),
                padding = new RectOffset(Mathf.RoundToInt(14 * scale), Mathf.RoundToInt(14 * scale), Mathf.RoundToInt(12 * scale), Mathf.RoundToInt(10 * scale))
            };
            fieldStyle.normal.background = field;
            fieldStyle.hover.background = fieldHover;
            fieldStyle.focused.background = fieldFocus;
            var ink = new Color(0.247f, 0.227f, 0.196f);
            fieldStyle.normal.textColor = fieldStyle.hover.textColor = fieldStyle.focused.textColor = ink;

            areaStyle = new GUIStyle(fieldStyle) { wordWrap = true, alignment = TextAnchor.UpperLeft };

            buttonStyle = CreateButtonStyle(scale, secondary, secondaryHover, secondaryActive, ink);
            categoryStyle = CreateButtonStyle(scale, secondary, secondaryHover, secondaryActive, ink);
            categoryStyle.onNormal.background = selected;
            categoryStyle.onHover.background = selectedHover;
            categoryStyle.onActive.background = selectedHover;
            categoryStyle.onNormal.textColor = categoryStyle.onHover.textColor = categoryStyle.onActive.textColor = ink;
            categoryStyle.margin = new RectOffset(4, 4, 4, 4);

            primaryButtonStyle = CreateButtonStyle(scale, accent, accentHover, accentActive, new Color(0.17f, 0.15f, 0.13f));
            primaryButtonStyle.fontSize = Mathf.RoundToInt(16 * scale);
            closeButtonStyle = new GUIStyle(buttonStyle) { fontSize = Mathf.RoundToInt(14 * scale) };

            statusStyle = new GUIStyle(hintStyle) { fontStyle = FontStyle.Bold };
            statusStyle.normal.textColor = new Color(0.09f, 0.48f, 0.50f);
            validationStyle = new GUIStyle(hintStyle) { fontStyle = FontStyle.Bold };
            validationStyle.normal.textColor = new Color(0.70f, 0.23f, 0.20f);

            entryButtonStyle = CreateButtonStyle(scale, accent, accentHover, accentActive, new Color(0.17f, 0.15f, 0.13f));
            entryButtonStyle.fontSize = Mathf.RoundToInt((UsesMobileEntryLayout() ? 24 : 18) * scale);
            entryButtonStyle.margin = new RectOffset(0, 0, 0, 0);
            entryButtonStyle.padding = new RectOffset(0, 0, 0, 0);
        }

        private static bool UsesMobileEntryLayout()
        {
#if UNITY_IOS || UNITY_ANDROID
            return true;
#else
            return Application.isMobilePlatform;
#endif
        }

        private void DrawEntryButton()
        {
            EnsureStyles(Mathf.Clamp(Mathf.Min(Screen.width / 1280f, Screen.height / 900f), 0.9f, 1.25f) * settings.interfaceScale);
            var mobile = UsesMobileEntryLayout();
            var baseSize = SelectEntryButtonBaseSize(mobile, settings.desktopEntryButtonSize, settings.mobileEntryButtonSize);
            var size = Mathf.Clamp(baseSize * styleScale, mobile ? 48f : 32f, mobile ? 112f : 80f);
            var margin = Mathf.Max(10f, 14f * styleScale);
            var rect = GetEntryButtonRect(ToGuiSafeArea(Screen.safeArea, Screen.height), size, margin, settings.entryButtonCorner);

            var previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, settings.entryButtonOpacity);
            if (GUI.Button(rect, "!", entryButtonStyle))
            {
                RequestOpen();
                Event.current.Use();
            }
            GUI.color = previousColor;
        }

        private GUIStyle CreateButtonStyle(float scale, Texture2D normal, Texture2D hover, Texture2D active, Color text)
        {
            var style = new GUIStyle(GUI.skin.button)
            {
                fontSize = Mathf.RoundToInt(15 * scale),
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
