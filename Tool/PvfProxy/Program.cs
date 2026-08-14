using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

string host = "127.0.0.1";
int chPort = 7001;
int gamePort = 10011;
int chTargetPort = 7002;
int gameTargetPort = 10012;
const int channel100Port = 10161;
const int channel100TargetPort = 10162;
string logDir = ".";
string? serverPath = null;
string serverArgs = "";
string? serverWd = null;
string? gamePath = null;
string gameArgs = "";
string? gameWd = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--ch-port": chPort = int.Parse(args[++i]); break;
        case "--game-port": gamePort = int.Parse(args[++i]); break;
        case "--ch-target-port": chTargetPort = int.Parse(args[++i]); break;
        case "--game-target-port": gameTargetPort = int.Parse(args[++i]); break;
        case "--log-dir": logDir = args[++i]; break;
        case "--server": serverPath = args[++i]; break;
        case "--server-args": serverArgs = args[++i]; break;
        case "--server-wd": serverWd = args[++i]; break;
        case "--game": gamePath = args[++i]; break;
        case "--game-args": gameArgs = args[++i]; break;
        case "--game-wd": gameWd = args[++i]; break;
        default:
            if (!args[i].StartsWith("--"))
            {
                if (i == 0) host = args[i];
                else if (i == 1) chPort = int.Parse(args[i]);
                else if (i == 2) gamePort = int.Parse(args[i]);
                else if (i == 3) chTargetPort = int.Parse(args[i]);
                else if (i == 4) gameTargetPort = int.Parse(args[i]);
                else if (i == 5) logDir = args[i];
            }
            break;
    }
}

var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
var logPath = Path.Combine(logDir, $"pvfproxy_{ts}.log");
var logLock = new object();
void Log(string line)
{
    var entry = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
    Console.WriteLine(entry);
    lock (logLock) File.AppendAllText(logPath, entry + "\n");
}

Log("=== PvfProxy Launcher ===");
Log(
    $"Proxy: ch={chPort}->{host}:{chTargetPort}, " +
    $"game={gamePort}->{host}:{gameTargetPort}, " +
    $"ch100={channel100Port}->{host}:{channel100TargetPort}");
Log($"Log: {logPath}");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Process? serverProc = null;
Process? gameProc = null;

void KillAll()
{
    try
    {
        if (gameProc != null && !gameProc.HasExited)
        {
            Log("Killing game process...");
            gameProc.Kill(true);
            gameProc.Dispose();
            gameProc = null;
        }
    }
    catch { }
    try
    {
        if (serverProc != null && !serverProc.HasExited)
        {
            Log("Killing server process...");
            serverProc.Kill(true);
            serverProc.Dispose();
            serverProc = null;
        }
    }
    catch { }
}

try
{
    if (!string.IsNullOrEmpty(serverPath))
    {
        Log($"Starting server: {serverPath} {serverArgs}");
        var psi = new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = serverArgs,
            WorkingDirectory = serverWd ?? Path.GetDirectoryName(serverPath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        serverProc = Process.Start(psi);
        if (serverProc != null)
        {
            serverProc.OutputDataReceived += (_, e) => { if (e.Data != null) Log($"[SERVER] {e.Data}"); };
            serverProc.ErrorDataReceived += (_, e) => { if (e.Data != null) Log($"[SERVER] {e.Data}"); };
            serverProc.BeginOutputReadLine();
            serverProc.BeginErrorReadLine();
        }

        Log("Waiting for server to be ready...");
        for (int retry = 0; retry < 30; retry++)
        {
            if (cts.IsCancellationRequested) break;
            try
            {
                using var test = new TcpClient();
                await test.ConnectAsync(host, chTargetPort, cts.Token);
                Log("Server is ready (channel port connected).");
                break;
            }
            catch
            {
                await Task.Delay(1000, cts.Token);
            }
        }
    }

    var tasks = new List<Task>
    {
        RunProxy("CH", host, chPort, chTargetPort, cts.Token),
        RunProxy("GAME", host, gamePort, gameTargetPort, cts.Token),
        RunProxy(
            "GAME-CH100",
            host,
            channel100Port,
            channel100TargetPort,
            cts.Token),
    };

    await Task.Delay(300, cts.Token);

    if (!string.IsNullOrEmpty(gamePath))
    {
        Log($"Starting game: {gamePath} {gameArgs}");
        var psi = new ProcessStartInfo
        {
            FileName = gamePath,
            Arguments = gameArgs,
            WorkingDirectory = gameWd ?? Path.GetDirectoryName(gamePath),
            UseShellExecute = false,
        };
        gameProc = Process.Start(psi);
    }

    if (gameProc != null)
        tasks.Add(WatchGameExit(gameProc, cts));

    await Task.WhenAll(tasks);
}
catch (OperationCanceledException) { }
catch (Exception ex)
{
    Log($"Fatal error: {ex.Message}");
}
finally
{
    Log("Shutting down...");
    cts.Cancel();
    KillAll();
    Log("Done.");
}

async Task WatchGameExit(Process proc, CancellationTokenSource source)
{
    try
    {
        await proc.WaitForExitAsync(source.Token);
        Log("Game process exited, shutting down launcher...");
    }
    catch (OperationCanceledException) { }
    finally
    {
        source.Cancel();
    }
}

async Task RunProxy(string tag, string h, int listenPort, int targetPort, CancellationToken ct)
{
    var listener = new TcpListener(IPAddress.Any, listenPort);
    listener.Start();
    Log($"[{tag}] Listening on 0.0.0.0:{listenPort} -> {h}:{targetPort}");

    try
    {
        int connId = 0;
        while (!ct.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(ct);
            var id = Interlocked.Increment(ref connId);
            _ = RelayConnectionAsync(tag, id, client, h, targetPort, ct);
        }
    }
    catch (OperationCanceledException) { }
    finally { listener.Stop(); }
}

async Task RelayConnectionAsync(string tag, int id, TcpClient client, string h, int port, CancellationToken ct)
{
    Log($"[{tag}#{id}] +Connection from {client.Client.RemoteEndPoint}");

    using (client)
    {
        try
        {
            using var server = new TcpClient();
            await server.ConnectAsync(h, port, ct);
            using var serverStream = server.GetStream();
            using var clientStream = client.GetStream();

            var t1 = RelayAsync($"{tag}#{id}", "C->S", clientStream, serverStream, ct);
            var t2 = RelayAsync($"{tag}#{id}", "S->C", serverStream, clientStream, ct);
            await Task.WhenAny(t1, t2);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"[{tag}#{id}] Error: {ex.Message}");
        }
    }

    Log($"[{tag}#{id}] -Disconnected");
}

async Task RelayAsync(string tag, string dir, NetworkStream from, NetworkStream to, CancellationToken ct)
{
    var buf = new byte[65536];
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var read = await from.ReadAsync(buf, 0, buf.Length, ct);
            if (read == 0) break;
            await to.WriteAsync(buf, 0, read, ct);
            await to.FlushAsync(ct);
            DumpPacket(tag, dir, buf.AsSpan(0, read));
        }
    }
    catch (Exception ex) when (ex is not OperationCanceledException and not IOException) { Log($"[{tag}] {dir} error: {ex.Message}"); }
}

void DumpPacket(string tag, string dir, ReadOnlySpan<byte> data)
{
    if (data.Length < 3) return;
    var cmd = data[0];
    var type = (ushort)(data[1] | (data[2] << 8));
    var hex = BitConverter.ToString(data[..Math.Min(data.Length, 64)].ToArray()).Replace("-", " ");
    var trunc = data.Length > 64 ? $" ({data.Length}B total, showing first 64)" : $" ({data.Length}B)";
    Log($"[{tag}] {dir} cmd=0x{cmd:X2} type=0x{type:X4}{trunc}\n    hex={hex}");
}
