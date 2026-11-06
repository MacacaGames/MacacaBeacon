using System;
using UnityEngine;

namespace MacacaGames.RuntimeBugReporter
{
    // All native keyboard differences live here, outside form layout and fields.
    internal sealed class MobileKeyboard : IDisposable
    {
        internal static bool Supported => Application.isMobilePlatform;
        internal bool Visible { get; private set; }
        internal float AvailableHeight { get; private set; }
        internal TouchScreenKeyboard Keyboard { get; private set; }
        private int _sampleFrame = -1;
#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaClass _bridge;
        private bool _bridgeUnavailable;
#endif

        internal void Track(TouchScreenKeyboard keyboard)
        {
            if (keyboard != null) Keyboard = keyboard;
        }

        internal TouchScreenKeyboard Open(string text, bool multiline, int limit)
        {
            if (Keyboard != null) Keyboard.active = false;
            Keyboard = TouchScreenKeyboard.Open(text, TouchScreenKeyboardType.Default, true, multiline);
            if (Keyboard != null) Keyboard.characterLimit = limit;
            return Keyboard;
        }

        internal void Sample()
        {
            if (_sampleFrame == Time.frameCount) return;
            _sampleFrame = Time.frameCount;
            Visible = Supported && TouchScreenKeyboard.visible;
            // A conservative editable region keeps navigation reachable if the OS
            // cannot report geometry. Known geometry always replaces this estimate.
            AvailableHeight = Visible ? Screen.height * 0.4f : Screen.height;
            if (!Supported) return;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_bridgeUnavailable)
            {
                try
                {
                    if (_bridge == null) _bridge = new AndroidJavaClass("com.macacagames.beacon.MacacaBeaconKeyboard");
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                        _bridge.CallStatic("requestSample", activity, TouchScreenKeyboard.visible);
                    var sample = _bridge.CallStatic<int[]>("getSample");
                    if (sample != null && sample.Length == 4 && sample[3] > 0)
                    {
                        Visible |= sample[0] != 0;
                        if (sample[0] != 0)
                            AvailableHeight = MobileFormNavigation.AvailableHeight(sample[1], sample[2], sample[3], Screen.height);
                    }
                }
                catch (Exception exception)
                {
                    _bridgeUnavailable = true;
                    Debug.LogWarning("Beacon keyboard geometry unavailable; manual scrolling remains available. " + exception.Message);
                }
            }
#elif UNITY_IOS && !UNITY_EDITOR
            var area = TouchScreenKeyboard.area;
            if (Visible && area.height > 0f)
                AvailableHeight = Mathf.Clamp(Screen.height - area.yMax, 0f, Screen.height);
#endif
        }

        internal void Hide()
        {
            if (Keyboard != null) Keyboard.active = false;
            Keyboard = null;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_bridge != null && !_bridgeUnavailable)
            {
                try
                {
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                        _bridge.CallStatic("hide", activity);
                }
                catch (Exception) { /* Geometry fallback must not prevent closing the form. */ }
            }
#endif
        }

        public void Dispose()
        {
            Hide();
#if UNITY_ANDROID && !UNITY_EDITOR
            _bridge?.Dispose();
            _bridge = null;
#endif
        }
    }
}
