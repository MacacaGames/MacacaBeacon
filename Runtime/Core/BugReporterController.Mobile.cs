using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    internal sealed partial class BugReporterController
    {
        private readonly MobileKeyboard mobileKeyboard = new MobileKeyboard();
        private readonly Rect[] mobileFieldRects = new Rect[MobileFormNavigation.Controls.Length];
        private int activeMobileField = -1;
        private TextEditor mobileTextEditor;
        private bool mobileKeyboardVisible;
        private float mobileAvailableHeight;
        private bool mobileKeyboardWasVisible;
        private float mobileKeyboardRequestedAt;
        private float mobileBackConsumedAt = -10f;
        private bool clearMobileFocus;
        private int revealMobileFieldAfterFrame = -1;
        private Rect mobileFormViewport;
        private bool screenshotExpanded;
        private int pendingScreenshotExpansion = -1;
        private Vector2 scrollBeforeScreenshotExpansion;
        private bool thumbnailPressed;
        private bool thumbnailDragged;
        private Vector2 thumbnailPressPosition;

        private void ResetMobileForm()
        {
            if (MobileKeyboard.Supported) DismissMobileKeyboard();
            activeMobileField = -1;
            mobileKeyboardVisible = false;
            mobileKeyboardWasVisible = false;
            mobileAvailableHeight = Screen.height;
            pendingFocusControl = null;
            revealMobileFieldAfterFrame = -1;
            mobileFormViewport = default;
            System.Array.Clear(mobileFieldRects, 0, mobileFieldRects.Length);
            screenshotExpanded = !MobileKeyboard.Supported;
            pendingScreenshotExpansion = -1;
            thumbnailPressed = false;
            if (MobileKeyboard.Supported)
            {
                formScroll = Vector2.zero;
                contentScroll = Vector2.zero;
            }
        }

        private string MobileFieldValue(int index)
        {
            switch (index)
            {
                case 0: return title;
                case 1: return description;
                default: return reporter;
            }
        }

        private void SyncMobileText()
        {
            var keyboard = mobileKeyboard.Keyboard;
            if (activeMobileField < 0 || keyboard == null) return;
            var text = keyboard.text ?? "";
            var limit = activeMobileField == 1 ? 2000 : 120;
            if (text.Length > limit) text = text.Substring(0, limit);
            switch (activeMobileField)
            {
                case 0: title = text; break;
                case 1: description = text; break;
                case 2: reporter = text; break;
            }
        }

        private void DismissMobileKeyboard()
        {
            SyncMobileText();
            if (mobileTextEditor != null) mobileTextEditor.keyboardOnScreen = null;
            mobileKeyboard.Hide();
            mobileTextEditor = null;
            activeMobileField = -1;
            pendingFocusControl = null;
            revealMobileFieldAfterFrame = -1;
            clearMobileFocus = true;
        }

        private void PrepareMobileForm(Event current)
        {
            if (!MobileKeyboard.Supported) return;
            SyncMobileText();
            if (current.type == EventType.MouseDown && mobileFormViewport.Contains(current.mousePosition))
            {
                for (var index = 0; index < mobileFieldRects.Length; index++)
                    if (mobileFieldRects[index].Contains(current.mousePosition))
                        revealMobileFieldAfterFrame = Time.frameCount + 1;
            }
            if (clearMobileFocus)
            {
                GUI.FocusControl(null);
                GUIUtility.keyboardControl = 0;
                clearMobileFocus = false;
            }
            // Latch geometry on Layout so every input/repaint pass uses the same layout.
            if (current.type != EventType.Layout) return;
            mobileKeyboard.Sample();
            var visible = !isDraftInbox && (mobileKeyboard.Visible || activeMobileField >= 0);
            var height = mobileKeyboard.Visible ? mobileKeyboard.AvailableHeight : Screen.height;
            if (visible != mobileKeyboardVisible || Mathf.Abs(height - mobileAvailableHeight) > 1f)
                revealMobileFieldAfterFrame = Time.frameCount + 1;
            mobileKeyboardVisible = visible;
            mobileAvailableHeight = height;
            if (mobileKeyboard.Visible) mobileKeyboardWasVisible = true;
            // Android may consume Back itself before Unity receives an Escape event.
            var keyboard = mobileKeyboard.Keyboard;
            var ended = keyboard != null && keyboard.status != TouchScreenKeyboard.Status.Visible;
            if (activeMobileField >= 0 && Time.realtimeSinceStartup - mobileKeyboardRequestedAt > 0.4f
                && (ended || (mobileKeyboardWasVisible && !mobileKeyboard.Visible)))
            {
                DismissMobileKeyboard();
                mobileKeyboardVisible = false;
                mobileKeyboardWasVisible = false;
                mobileBackConsumedAt = Time.realtimeSinceStartup;
            }
            if (pendingScreenshotExpansion >= 0)
            {
                var expand = pendingScreenshotExpansion == 1;
                pendingScreenshotExpansion = -1;
                if (expand)
                {
                    scrollBeforeScreenshotExpansion = formScroll;
                    DismissMobileKeyboard();
                }
                else
                {
                    screenshotAnnotator?.EndStroke();
                    touchAnnotationId = -1;
                    formScroll = scrollBeforeScreenshotExpansion;
                }
                screenshotExpanded = expand;
                screenshotImageRectValid = false;
            }
        }

        private void TrackMobileField(int index, Rect rect)
        {
            if (!MobileKeyboard.Supported || Event.current.type == EventType.Layout) return;
            if (Event.current.type == EventType.Repaint) mobileFieldRects[index] = ToScreenRect(rect);
            if (GUI.GetNameOfFocusedControl() != MobileFormNavigation.Controls[index]) return;
            var editor = (TextEditor)GUIUtility.GetStateObject(typeof(TextEditor), GUIUtility.keyboardControl);
            if (activeMobileField != index)
            {
                // IMGUI opens keyboards on physical taps only. Programmatic navigation
                // must attach a keyboard to the target TextEditor explicitly.
                if (mobileTextEditor != null) mobileTextEditor.keyboardOnScreen = null;
                if (editor.keyboardOnScreen == null)
                    editor.keyboardOnScreen = mobileKeyboard.Open(MobileFieldValue(index), index == 1, index == 1 ? 2000 : 120);
                else
                    mobileKeyboard.Track(editor.keyboardOnScreen);
                activeMobileField = index;
                mobileTextEditor = editor;
                mobileKeyboardRequestedAt = Time.realtimeSinceStartup;
                mobileKeyboardWasVisible = false;
                revealMobileFieldAfterFrame = Time.frameCount + 1;
            }
            else
                mobileKeyboard.Track(editor.keyboardOnScreen);
        }

        private void RevealMobileField()
        {
            if (activeMobileField < 0 || revealMobileFieldAfterFrame < 0
                || Time.frameCount < revealMobileFieldAfterFrame || mobileFormViewport.height <= 0f) return;
            var target = mobileFieldRects[activeMobileField];
            if (target.height <= 0f) return;
            var margin = 12f * styleScale;
            var labelHeight = 30f * styleScale;
            if (target.height + labelHeight > mobileFormViewport.height - 2f * margin && mobileTextEditor != null)
            {
                var cursorIndex = mobileTextEditor.cursorIndex;
                var keyboard = mobileKeyboard.Keyboard;
                if (keyboard != null && keyboard.canGetSelection) cursorIndex = keyboard.selection.start;
                var value = MobileFieldValue(activeMobileField);
                var cursor = areaStyle.GetCursorPixelPosition(new Rect(0f, 0f, target.width, target.height),
                    new GUIContent(value), Mathf.Clamp(cursorIndex, 0, value.Length));
                target.y += cursor.y - mobileTextEditor.scrollOffset.y;
                target.height = Mathf.Max(areaStyle.lineHeight, 20f * styleScale);
            }
            else
            {
                target.yMin -= labelHeight;
            }
            formScroll.y = MobileFormNavigation.RevealScroll(formScroll.y, target, mobileFormViewport, margin);
            revealMobileFieldAfterFrame = -1;
        }

        private void DrawMobileKeyboardToolbar()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(8f * styleScale);
            GUI.enabled = !isSending && activeMobileField > 0;
            if (SoftwareCursorButton("上一欄", buttonStyle, GUILayout.Height(48f * styleScale), GUILayout.ExpandWidth(true)))
                MoveMobileField(-1);
            GUI.enabled = !isSending && activeMobileField >= 0 && activeMobileField < MobileFormNavigation.Controls.Length - 1;
            if (SoftwareCursorButton("下一欄", buttonStyle, GUILayout.Height(48f * styleScale), GUILayout.ExpandWidth(true)))
                MoveMobileField(1);
            GUI.enabled = true;
            if (SoftwareCursorButton("收鍵盤", buttonStyle, GUILayout.Height(48f * styleScale), GUILayout.ExpandWidth(true)))
                DismissMobileKeyboard();
            GUILayout.Space(8f * styleScale);
            GUILayout.EndHorizontal();
        }

        private void MoveMobileField(int delta)
        {
            SyncMobileText();
            var next = MobileFormNavigation.Adjacent(activeMobileField, delta);
            if (next >= 0) pendingFocusControl = MobileFormNavigation.Controls[next];
        }

        private void DrawScreenshotThumbnail(float width)
        {
            screenshotImageRectValid = false;
            var rect = GUILayoutUtility.GetRect(width, 96f * styleScale, GUILayout.Height(96f * styleScale));
            GUI.Box(rect, GUIContent.none, buttonStyle);
            var image = new Rect(rect.x + 6f * styleScale, rect.y + 6f * styleScale, 72f * styleScale, rect.height - 12f * styleScale);
            if (screenshotPreview != null) GUI.DrawTexture(image, screenshotPreview, ScaleMode.ScaleToFit, false);
            var text = new Rect(image.xMax + 12f * styleScale, rect.y, Mathf.Max(1f, rect.xMax - image.xMax - 18f * styleScale), rect.height);
            GUI.Label(text, screenshotPreview != null ? "SCREENSHOT\n點擊放大與標註" : "Screenshot unavailable", labelStyle);
            var current = Event.current;
            var id = GUIUtility.GetControlID("BeaconScreenshotThumbnail".GetHashCode(), FocusType.Passive, rect);
            switch (current.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (GUI.enabled && rect.Contains(current.mousePosition) && current.button == 0)
                    {
                        thumbnailPressed = true;
                        thumbnailDragged = false;
                        thumbnailPressPosition = current.mousePosition;
                        GUIUtility.hotControl = id;
                        current.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (thumbnailPressed && GUIUtility.hotControl == id)
                    {
                        thumbnailDragged |= (current.mousePosition - thumbnailPressPosition).sqrMagnitude > 100f * styleScale * styleScale;
                        current.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (thumbnailPressed && GUIUtility.hotControl == id)
                    {
                        if (!thumbnailDragged && rect.Contains(current.mousePosition)) pendingScreenshotExpansion = 1;
                        thumbnailPressed = false;
                        GUIUtility.hotControl = 0;
                        current.Use();
                    }
                    break;
            }
        }
    }
}
