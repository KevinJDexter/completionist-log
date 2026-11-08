using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProgressExporter;

/// <summary>
/// Serves your progress to the Completionist Log app on your home network.
/// TCP port (default 47823): GET /ping and GET /export, each needing the pair code.
/// UDP port+1: answers "PROGEX?" broadcasts so the app can find this PC.
/// Only private network addresses are answered.
/// </summary>
internal sealed class WifiServer : IDisposable
{
    private readonly Plugin plugin;
    private readonly CancellationTokenSource cts = new();
    private TcpListener? tcp;
    private UdpClient? udp;

    public WifiServer(Plugin plugin) { this.plugin = plugin; }

    public void Start()
    {
        var port = plugin.Config.Port;
        try
        {
            tcp = new TcpListener(IPAddress.Any, port);
            tcp.Start();
            _ = Task.Run(() => AcceptLoop(cts.Token));
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, port + 1)) { EnableBroadcast = true };
            _ = Task.Run(() => DiscoveryLoop(cts.Token));
            Plugin.Log.Information($"Wi-Fi sync listening on {port}/{port + 1}");
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Wi-Fi sync could not start");
            Plugin.Chat.PrintError($"[Progress Exporter] Wi-Fi sync couldn't start on port {port}: {e.Message}");
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        try { tcp?.Stop(); } catch { }
        try { udp?.Dispose(); } catch { }
    }

    private static bool IsPrivate(IPAddress a)
    {
        if (IPAddress.IsLoopback(a)) return true;
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public static List<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
            .Select(a => a.ToString()).Distinct().ToList();

    private async Task DiscoveryLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && udp != null)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                if (!IsPrivate(r.RemoteEndPoint.Address)) continue;
                if (Encoding.ASCII.GetString(r.Buffer).Trim() != "PROGEX?") continue;
                var reply = Encoding.ASCII.GetBytes($"PROGEX!{plugin.Config.Port}");
                await udp.SendAsync(reply, reply.Length, r.RemoteEndPoint);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception e) { Plugin.Log.Warning(e, "discovery"); }
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && tcp != null)
        {
            TcpClient client;
            try { client = await tcp.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception e) { Plugin.Log.Warning(e, "accept"); continue; }
            _ = Task.Run(() => Handle(client, ct));
        }
    }

    private async Task Handle(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000; client.SendTimeout = 15000;
                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                var stream = client.GetStream();
                if (remote == null || !IsPrivate(remote)) { await Send(stream, 403, "{\"error\":\"local network only\"}"); return; }

                // read the request head (small, GET only)
                var buf = new byte[8192]; int len = 0;
                while (len < buf.Length)
                {
                    int n = await stream.ReadAsync(buf.AsMemory(len, buf.Length - len), ct);
                    if (n <= 0) break; len += n;
                    if (Encoding.ASCII.GetString(buf, 0, len).Contains("\r\n\r\n")) break;
                }
                var head = Encoding.ASCII.GetString(buf, 0, len).Split("\r\n");
                var parts = head[0].Split(' ');
                if (parts.Length < 2 || parts[0] != "GET") { await Send(stream, 405, "{\"error\":\"GET only\"}"); return; }
                var path = parts[1].Split('?')[0];
                var code = head.Skip(1).Select(h => h.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("X-Progex-Code", StringComparison.OrdinalIgnoreCase)).Select(p => p[1].Trim()).FirstOrDefault() ?? "";
                var want = plugin.Config.PairCode;
                if (want.Length == 0 || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code.ToUpperInvariant()), Encoding.UTF8.GetBytes(want)))
                { await Send(stream, 401, "{\"error\":\"wrong pair code\"}"); return; }

                if (path == "/ping") { await Send(stream, 200, "{\"app\":\"progex\",\"ok\":true}"); return; }
                if (path != "/export") { await Send(stream, 404, "{\"error\":\"not found\"}"); return; }

                Dictionary<string, object>? data = await Plugin.Framework.RunOnFrameworkThread(() => plugin.CanExport() ? plugin.Collect(false) : null);
                if (data == null) { await Send(stream, 409, "{\"error\":\"not logged in\"}"); return; }
                await Send(stream, 200, JsonSerializer.Serialize(data));
                await Plugin.Framework.RunOnFrameworkThread(() => Plugin.Chat.Print("[Progress Exporter] Sent your progress to the Completionist Log app."));
            }
            catch (Exception e) { Plugin.Log.Warning(e, "request"); }
        }
    }

    private static async Task Send(NetworkStream s, int status, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var reason = status switch { 200 => "OK", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 409 => "Conflict", _ => "Error" };
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await s.WriteAsync(head);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    public static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var b = RandomNumberGenerator.GetBytes(6);
        return new string(b.Select(x => alphabet[x % alphabet.Length]).ToArray());
    }
}
