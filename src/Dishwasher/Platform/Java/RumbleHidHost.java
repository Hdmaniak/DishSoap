// ============================================================================
// RumbleHidHost -- Bluetooth HID gamepad rumble for Android < 12 (API < 31).
//
// Why: Android only exposes a gamepad vibrator from API 31
// (InputDevice.getVibratorManager()).  Before that there is no public API, and
// the kernel evdev node for a Bluetooth Xbox pad has no force-feedback
// capability, so InputDevice.getVibrator() returns NullVibrator.  The ONLY
// route to a Bluetooth controller on API 29 is the platform HID host's
// output-report path (BluetoothHidHost.sendData), which is @hide and
// greylist-max-o.
//
// This helper (a) applies the well-known VMRuntime.setHiddenApiExemptions
// bypass so the @hide method can be reflected on a targetSdk >= 28 app, and
// (b) sends the Xbox One (model 1708) Bluetooth rumble output report, ported
// from SDL's SDL_hidapi_xboxone.c:
//     03 0F LT RT LM RM FF 00 EB     (motors 0..100)
// Requires android.permission.BLUETOOTH + BLUETOOTH_ADMIN (API <= 30).
//
// Called from C# Platform/BluetoothHidRumble.cs.  Revert: delete this file +
// BluetoothHidRumble.cs and the call in AndroidRumble.Initialize.
// ============================================================================
package com.recomp.dishwasher;

import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothProfile;
import android.content.Context;
import android.util.Log;
import java.lang.reflect.Method;
import java.util.List;

public final class RumbleHidHost {
    private static final String TAG = "Dishwasher";
    private static final int PROFILE_HID_HOST = 4; // BluetoothProfile.HID_HOST (@hide)

    private static BluetoothProfile sProxy;
    private static boolean sExempted;
    private static Method sSendData;

    private static final BluetoothProfile.ServiceListener LISTENER =
            new BluetoothProfile.ServiceListener() {
                @Override
                public void onServiceConnected(int profile, BluetoothProfile proxy) {
                    sProxy = proxy;
                    try {
                        if (!sExempted) {
                            sExempted = exemptHiddenApi("Landroid/bluetooth/BluetoothHidHost;");
                        }
                        sSendData = proxy.getClass().getMethod(
                                "sendData", BluetoothDevice.class, String.class);
                        Log.i(TAG, "[rumble][bthid] HID host connected; sendData="
                                + (sSendData != null));
                    } catch (Throwable t) {
                        Log.e(TAG, "[rumble][bthid] onServiceConnected: " + t);
                    }
                }

                @Override
                public void onServiceDisconnected(int profile) {
                    sProxy = null;
                    sSendData = null;
                    Log.i(TAG, "[rumble][bthid] HID host disconnected");
                }
            };

    private RumbleHidHost() {}

    /** Bind the HID host profile and prepare sendData(). Safe to call once. */
    public static void init(Context ctx) {
        try {
            BluetoothAdapter adapter = BluetoothAdapter.getDefaultAdapter();
            if (adapter == null) {
                Log.i(TAG, "[rumble][bthid] no bluetooth adapter");
                return;
            }
            boolean ok = adapter.getProfileProxy(ctx, LISTENER, PROFILE_HID_HOST);
            Log.i(TAG, "[rumble][bthid] getProfileProxy=" + ok);
        } catch (Throwable t) {
            Log.e(TAG, "[rumble][bthid] init: " + t);
        }
    }

    /** True once the HID host proxy is bound and sendData() is reflectable. */
    public static boolean isReady() {
        return sProxy != null && sSendData != null;
    }

    /**
     * Send an Xbox One BT rumble report. nameHint is the InputDevice name to
     * match (may be null/empty = first device); hex is the output report as
     * hex, e.g. "030F00005050FF00EB".
     * @return true if the report was handed to the HID stack
     */
    public static boolean send(String nameHint, String hex) {
        try {
            BluetoothProfile proxy = sProxy;
            Method send = sSendData;
            if (proxy == null || send == null) {
                Log.e(TAG, "[rumble][bthid] send: not ready proxy=" + (proxy != null)
                        + " sendData=" + (send != null));
                return false;
            }
            List<BluetoothDevice> devices = proxy.getConnectedDevices();
            StringBuilder names = new StringBuilder();
            if (devices != null) {
                for (BluetoothDevice d : devices) {
                    names.append("'").append(d.getName()).append("' ");
                }
            }
            BluetoothDevice target = pick(proxy, nameHint);
            if (target == null) {
                Log.e(TAG, "[rumble][bthid] send: no target; connected=[" + names + "]");
                return false;
            }
            Object r = send.invoke(proxy, target, hex);
            boolean ok = (r instanceof Boolean) && (Boolean) r;
            Log.i(TAG, "[rumble][bthid] sendData target='" + target.getName() + "' hex=" + hex
                    + " connected=[" + names + "] -> " + ok);
            return ok;
        } catch (Throwable t) {
            Log.e(TAG, "[rumble][bthid] send", t);
            return false;
        }
    }

    private static BluetoothDevice pick(BluetoothProfile proxy, String nameHint) {
        List<BluetoothDevice> devices = proxy.getConnectedDevices();
        if (devices == null || devices.isEmpty()) {
            return null;
        }
        BluetoothDevice first = devices.get(0);
        if (nameHint == null) {
            return first;
        }
        for (BluetoothDevice d : devices) {
            if (d != null && nameHint.equals(d.getName())) {
                return d;
            }
        }
        return first;
    }

    /**
     * FreeReflection-style VMRuntime.setHiddenApiExemptions bypass, needed
     * because BluetoothHidHost.sendData is greylist-max-o and our targetSdk
     * is >= 28. Works on Android 9..11; not used on API >= 31.
     */
    private static boolean exemptHiddenApi(String prefix) {
        try {
            Method forName = Class.class.getDeclaredMethod("forName", String.class);
            Method getDeclaredMethod = Class.class.getDeclaredMethod(
                    "getDeclaredMethod", String.class, Class[].class);
            Class<?> vmRuntimeClass = (Class<?>) forName.invoke(null, "dalvik.system.VMRuntime");
            Method getRuntime = (Method) getDeclaredMethod.invoke(vmRuntimeClass, "getRuntime", null);
            Method setHiddenApiExemptions = (Method) getDeclaredMethod.invoke(
                    vmRuntimeClass, "setHiddenApiExemptions", new Class[]{String[].class});
            Object vmRuntime = getRuntime.invoke(null);
            setHiddenApiExemptions.invoke(vmRuntime, new Object[]{new String[]{prefix}});
            Log.i(TAG, "[rumble][bthid] hidden-api exemption applied: " + prefix);
            return true;
        } catch (Throwable t) {
            Log.e(TAG, "[rumble][bthid] hidden-api exemption failed: " + t);
            return false;
        }
    }
}
