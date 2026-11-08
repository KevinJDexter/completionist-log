package io.github.kevinjdexter.completionistlog;

import android.app.Activity;
import android.content.Intent;
import android.webkit.CookieManager;

import androidx.activity.result.ActivityResult;

import com.getcapacitor.JSObject;
import com.getcapacitor.Plugin;
import com.getcapacitor.PluginCall;
import com.getcapacitor.PluginMethod;
import com.getcapacitor.annotation.ActivityCallback;
import com.getcapacitor.annotation.CapacitorPlugin;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

/**
 * Reads Lodestone pages from the phone. Requests carry the cookies of the in-app
 * Square Enix sign-in (LodestoneLoginActivity), so the owner-only pages (quests,
 * orchestrion, Triple Triad, Blue Magic, Trust) can be read once you've signed in.
 * Nothing leaves the phone except the requests to finalfantasyxiv.com.
 */
@CapacitorPlugin(name = "Lodestone")
public class LodestonePlugin extends Plugin {

    @PluginMethod
    public void get(final PluginCall call) {
        final String url = call.getString("url");
        final String ua = call.getString("userAgent", "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36");
        if (url == null || !url.startsWith("https://") || !url.contains(".finalfantasyxiv.com/")) {
            call.reject("Only Lodestone pages can be read");
            return;
        }
        new Thread(() -> {
            HttpURLConnection con = null;
            try {
                con = (HttpURLConnection) new URL(url).openConnection();
                con.setConnectTimeout(15000);
                con.setReadTimeout(25000);
                con.setInstanceFollowRedirects(true);
                con.setRequestProperty("User-Agent", ua);
                con.setRequestProperty("Accept-Language", "en-US,en;q=0.9");
                con.setRequestProperty("Accept", "text/html,application/xhtml+xml");
                String cookies = CookieManager.getInstance().getCookie(url);
                if (cookies != null && !cookies.isEmpty()) con.setRequestProperty("Cookie", cookies);
                int status = con.getResponseCode();
                InputStream in = status >= 400 ? con.getErrorStream() : con.getInputStream();
                String body = "";
                if (in != null) {
                    ByteArrayOutputStream out = new ByteArrayOutputStream();
                    byte[] buf = new byte[16384];
                    int n;
                    while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                    in.close();
                    body = new String(out.toByteArray(), StandardCharsets.UTF_8);
                }
                JSObject ret = new JSObject();
                ret.put("status", status);
                ret.put("url", con.getURL().toString());
                ret.put("data", body);
                call.resolve(ret);
            } catch (Exception e) {
                call.reject("Network error: " + e.getMessage());
            } finally {
                if (con != null) con.disconnect();
            }
        }).start();
    }

    @PluginMethod
    public void signIn(PluginCall call) {
        String url = call.getString("url", "https://na.finalfantasyxiv.com/lodestone/account/login/");
        Intent intent = new Intent(getContext(), LodestoneLoginActivity.class);
        intent.putExtra("url", url);
        startActivityForResult(call, intent, "signInResult");
    }

    @ActivityCallback
    private void signInResult(PluginCall call, ActivityResult result) {
        if (call == null) return;
        CookieManager.getInstance().flush();
        JSObject ret = new JSObject();
        ret.put("finished", result.getResultCode() == Activity.RESULT_OK);
        call.resolve(ret);
    }

    @PluginMethod
    public void signOut(PluginCall call) {
        CookieManager cm = CookieManager.getInstance();
        cm.removeAllCookies(ok -> {
            cm.flush();
            call.resolve();
        });
    }
}
