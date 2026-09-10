package com.androiddevmonitor.companion;

import android.util.Log;

/**
 * Optional Android Dev Monitor companion SDK.
 *
 * <p>Copy this single file into your app, call the methods at the places you care about, and the
 * desktop tool (Developer Tools -> Companion event report) turns them into a timeline.
 *
 * <p>Output format: {@code ADM_COMPANION|kind|name|value|unit|payload}
 */
public final class AdmCompanion {

    private static final String TAG = "AdmCompanion";
    private static final String MARKER = "ADM_COMPANION";

    private static volatile boolean enabled = true;

    private AdmCompanion() {
    }

    /** Turn all companion logging on or off, for example from a debug flag. */
    public static void setEnabled(boolean value) {
        enabled = value;
    }

    /** A named moment in time, for example {@code mark("feed loaded")}. */
    public static void mark(String name) {
        emit("mark", name, null, null, null);
    }

    /** A numeric measurement, for example {@code metric("feed_items", 42, "items")}. */
    public static void metric(String name, double value, String unit) {
        emit("metric", name, String.valueOf(value), unit, null);
    }

    /** A duration in milliseconds, for example {@code duration("checkout", 412)}. */
    public static void duration(String name, long milliseconds) {
        emit("duration", name, String.valueOf(milliseconds), "ms", null);
    }

    /** Anything else: background work, retries, cache hits, custom stages. */
    public static void event(String kind, String name, String value, String unit, String payload) {
        emit(kind, name, value, unit, payload);
    }

    private static void emit(String kind, String name, String value, String unit, String payload) {
        if (!enabled) {
            return;
        }
        StringBuilder builder = new StringBuilder(MARKER);
        builder.append('|').append(sanitize(kind));
        builder.append('|').append(sanitize(name));
        builder.append('|').append(value == null ? "" : sanitize(value));
        builder.append('|').append(unit == null ? "" : sanitize(unit));
        if (payload != null && !payload.isEmpty()) {
            builder.append('|').append(sanitize(payload));
        }
        Log.i(TAG, builder.toString());
    }

    private static String sanitize(String text) {
        return text.replace('|', '/').replace('\n', ' ').trim();
    }
}
