package com.macacagames.beacon;

import android.app.Activity;
import android.content.Context;
import android.graphics.Rect;
import android.os.Build;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.view.WindowInsets;
import android.view.inputmethod.InputMethodManager;

/** Samples views on the UI thread, publishes one immutable snapshot to Unity. */
public final class MacacaBeaconKeyboard {
    private static volatile int[] sample = new int[4];
    private static volatile boolean pending;

    public static int[] getSample() { return sample; }

    public static void requestSample(final Activity activity, final boolean unityVisible) {
        if (activity == null || pending) return;
        pending = true;
        activity.runOnUiThread(() -> {
            try {
                View decor = activity.getWindow().getDecorView();
                View surface = findSurface(decor);
                if (surface == null) surface = activity.findViewById(android.R.id.content);
                if (surface == null || surface.getHeight() == 0) return;
                int[] surfaceLocation = new int[2];
                int[] decorLocation = new int[2];
                surface.getLocationOnScreen(surfaceLocation);
                decor.getLocationOnScreen(decorLocation);
                Rect frame = new Rect();
                decor.getWindowVisibleDisplayFrame(frame);
                int top = frame.bottom;
                boolean visible = unityVisible || decorLocation[1] + decor.getHeight() - top > decor.getHeight() * 0.15f;
                if (Build.VERSION.SDK_INT >= 30) {
                    WindowInsets insets = decor.getRootWindowInsets();
                    if (insets != null) {
                        visible = insets.isVisible(WindowInsets.Type.ime());
                        int bottom = insets.getInsets(WindowInsets.Type.ime()).bottom;
                        // A resized window can report visibility without an overlap.
                        top = bottom > 0 ? activity.getWindowManager().getCurrentWindowMetrics().getBounds().bottom - bottom
                            : surfaceLocation[1] + surface.getHeight();
                    }
                }
                sample = new int[] { visible ? 1 : 0, top, surfaceLocation[1], surface.getHeight() };
            } catch (RuntimeException ignored) {
                // An Activity can disappear between the Unity request and this callback.
                sample = new int[4];
            } finally { pending = false; }
        });
    }

    private static View findSurface(View view) {
        if (view instanceof SurfaceView) return view;
        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup)view;
            for (int i = 0; i < group.getChildCount(); i++) {
                View result = findSurface(group.getChildAt(i));
                if (result != null) return result;
            }
        }
        return null;
    }

    public static void hide(final Activity activity) {
        if (activity == null) return;
        activity.runOnUiThread(() -> {
            View view = activity.getWindow().getDecorView();
            InputMethodManager manager = (InputMethodManager)activity.getSystemService(Context.INPUT_METHOD_SERVICE);
            if (manager != null) manager.hideSoftInputFromWindow(view.getWindowToken(), 0);
        });
    }
}
