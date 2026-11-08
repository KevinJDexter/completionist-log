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
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.SocketTimeoutException;
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

    // ---- Wi-Fi sync with the Progress Exporter plugin on your PC ----

    private static boolean isPrivateHost(String host) {
        if (host == null) return false;
        if (host.startsWith("10.") || host.startsWith("192.168.")) return true;
        if (host.startsWith("172.")) {
            try { int b = Integer.parseInt(host.split("\\.")[1]); return b >= 16 && b <= 31; } catch (Exception e) { return false; }
        }
        return false;
    }

    /** Broadcasts "PROGEX?" on the local network and returns the first PC that answers. */
    @PluginMethod
    public void discover(final PluginCall call) {
        final int port = call.getInt("port", 47823);
        new Thread(() -> {
            try (DatagramSocket sock = new DatagramSocket()) {
                sock.setBroadcast(true);
                sock.setSoTimeout(1500);
                byte[] msg = "PROGEX?".getBytes(StandardCharsets.US_ASCII);
                for (int attempt = 0; attempt < 2; attempt++) {
                    sock.send(new DatagramPacket(msg, msg.length, InetAddress.getByName("255.255.255.255"), port + 1));
                    try {
                        byte[] buf = new byte[64];
                        DatagramPacket p = new DatagramPacket(buf, buf.length);
                        sock.receive(p);
                        String r = new String(p.getData(), 0, p.getLength(), StandardCharsets.US_ASCII).trim();
                        if (r.startsWith("PROGEX!")) {
                            JSObject ret = new JSObject();
                            ret.put("ip", p.getAddress().getHostAddress());
                            ret.put("port", Integer.parseInt(r.substring(7).trim()));
                            call.resolve(ret);
                            return;
                        }
                    } catch (SocketTimeoutException ignored) { }
                }
                call.resolve(new JSObject());
            } catch (Exception e) {
                call.reject("Discovery failed: " + e.getMessage());
            }
        }).start();
    }

    /** GET from the plugin on your PC (home network addresses only). */
    @PluginMethod
    public void getLocal(final PluginCall call) {
        final String ip = call.getString("ip");
        final int port = call.getInt("port", 47823);
        final String path = call.getString("path", "/ping");
        final String code = call.getString("code", "");
        if (!isPrivateHost(ip)) { call.reject("Only home network addresses"); return; }
        new Thread(() -> {
            HttpURLConnection con = null;
            try {
                con = (HttpURLConnection) new URL("http://" + ip + ":" + port + path).openConnection();
                con.setConnectTimeout(4000);
                con.setReadTimeout(60000);
                con.setRequestProperty("X-Progex-Code", code);
                int status = con.getResponseCode();
                InputStream in = status >= 400 ? con.getErrorStream() : con.getInputStream();
                String body = "";
                if (in != null) {
                    ByteArrayOutputStream out = new ByteArrayOutputStream();
                    byte[] buf = new byte[16384]; int n;
                    while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                    in.close();
                    body = new String(out.toByteArray(), StandardCharsets.UTF_8);
                }
                JSObject ret = new JSObject();
                ret.put("status", status);
                ret.put("data", body);
                call.resolve(ret);
            } catch (Exception e) {
                call.reject("Couldn't reach your PC: " + e.getMessage());
            } finally {
                if (con != null) con.disconnect();
            }
        }).start();
    }
}
