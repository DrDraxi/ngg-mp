using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using BepInEx;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// Test automation for the two-copy end-to-end setup (scripts/e2e.ps1): a small HTTP API on 127.0.0.1 only, so a
    /// script can drive each game copy (host, join, any harness command, screenshots, quit) and read its state. Runs
    /// only with the test harness on (<c>[Debug] EnableTestHarness</c>) and <c>[Debug] DevControlPort</c> above 0.
    /// A plain TCP listener (http.sys would need a URL reservation); requests are read on a background thread and
    /// answered on Unity's main thread. GET only; every answer is JSON.
    /// <list type="table">
    /// <item><term>/status</term><description>Service state, role, players, scene, save folder.</description></item>
    /// <item><term>/cmd?line=&lt;command&gt;</term><description>A harness command (docs/TESTING.md 2.3), queued like a
    /// line of the command file; answered with its result once it ran (or "queued" after 60 s).</description></item>
    /// <item><term>/screenshot?name=</term><description>A PNG in BepInEx/screenshots; answered once it is on disk.</description></item>
    /// <item><term>/host</term>, <term>/join?host=&amp;port=</term>, <term>/leave</term><description>The session.</description></item>
    /// <item><term>/quit</term><description>Leaves the session, then quits the game (the harness's quit).</description></item>
    /// </list>
    /// </summary>
    internal sealed class DevControl : MonoBehaviour
    {
        private const double CommandTimeoutSeconds = 60;
        private const double ScreenshotTimeoutSeconds = 10;

        private readonly ConcurrentQueue<Request> _requests = new ConcurrentQueue<Request>();
        private readonly List<Job> _jobs = new List<Job>();
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private IMultiplayerService _service;
        private TestHarnessRunner _runner;

        public int Port { get; private set; }

        internal void Initialize(int port, IMultiplayerService service, TestHarnessRunner runner)
        {
            _service = service;
            _runner = runner;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port);
                _listener.Start();
            }
            catch (SocketException e)
            {
                NetLog.Error($"[DevControl] Port {port} is not free ({e.SocketErrorCode}); the dev control API is off.");
                _listener = null;
                return;
            }
            Port = port;
            _running = true;
            _thread = new Thread(Accept) { IsBackground = true, Name = "NGGMP dev control" };
            _thread.Start();
            // Test copies run side by side: one without focus must keep running (and keep its session alive).
            Application.runInBackground = true;
            // ...and still take keys sent through the API (key command): by default the Input System disables the
            // keyboard while the window has no focus, which drops them.
            UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
            NetLog.Warn($"[DevControl] Listening on http://127.0.0.1:{port}/ (test setups only).");
        }

        private void Accept()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 5000;
                    var reader = new StreamReader(client.GetStream(), Encoding.ASCII, false, 1024);
                    string line = reader.ReadLine() ?? string.Empty; // "GET /path?a=1 HTTP/1.1"
                    while (!string.IsNullOrEmpty(reader.ReadLine()))
                    {
                        // headers
                    }
                    var parts = line.Split(' ');
                    string target = parts.Length > 1 ? parts[1] : "/";
                    int q = target.IndexOf('?');
                    var request = new Request
                    {
                        Client = client,
                        Path = (q >= 0 ? target.Substring(0, q) : target).Trim('/').ToLowerInvariant(),
                        Query = ParseQuery(q >= 0 ? target.Substring(q + 1) : string.Empty)
                    };
                    _requests.Enqueue(request);
                }
                catch (Exception e)
                {
                    if (!_running)
                        return;
                    NetLog.Debug($"[DevControl] {e.Message}");
                    try
                    {
                        client?.Close();
                    }
                    catch (Exception)
                    {
                        // Already gone.
                    }
                }
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString((eq >= 0 ? pair.Substring(0, eq) : pair).Replace('+', ' '));
                string value = eq >= 0 ? Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' ')) : string.Empty;
                result[key] = value;
            }
            return result;
        }

        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = _jobs.Count - 1; i >= 0; i--)
            {
                var job = _jobs[i];
                string body;
                try
                {
                    body = job.Step(now);
                }
                catch (Exception e)
                {
                    body = Error(e.GetType().Name + ": " + e.Message);
                }
                if (body == null)
                    continue;
                _jobs.RemoveAt(i);
                Reply(job.Request, body);
            }
            while (_requests.TryDequeue(out var request))
            {
                string body;
                try
                {
                    body = Handle(request, now);
                }
                catch (Exception e)
                {
                    body = Error(e.GetType().Name + ": " + e.Message);
                }
                if (body != null)
                    Reply(request, body);
            }
        }

        /// <summary>The answer, or null when a job answers later.</summary>
        private string Handle(Request request, double now)
        {
            switch (request.Path)
            {
                case "":
                case "status":
                    return Status();
                case "cmd":
                {
                    string line = request.Arg("line");
                    string result = null;
                    if (_runner == null || !_runner.Submit(line, r => result = r))
                        return Error($"not a command, or the queue is full: '{line}'");
                    double until = now + CommandTimeoutSeconds;
                    _jobs.Add(new Job(request, t => result != null
                        ? new JsonBuilder().BeginObject().Value("line", line).Value("result", result).EndObject().ToString()
                        : t > until ? new JsonBuilder().BeginObject().Value("line", line).Value("queued", true).EndObject().ToString() : null));
                    return null;
                }
                case "screenshot":
                {
                    string name = Sanitize(request.Arg("name", "dev"));
                    string folder = Path.Combine(Paths.BepInExRootPath, "screenshots");
                    Directory.CreateDirectory(folder);
                    string file = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + name + ".png");
                    for (int n = 2; File.Exists(file); n++)
                        file = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + name + "-" + n + ".png");
                    ScreenCapture.CaptureScreenshot(file);
                    double until = now + ScreenshotTimeoutSeconds;
                    long lastSize = -1;
                    _jobs.Add(new Job(request, t =>
                    {
                        long size = File.Exists(file) ? new FileInfo(file).Length : -1;
                        bool stable = size > 0 && size == lastSize;
                        lastSize = size;
                        if (stable)
                            return new JsonBuilder().BeginObject().Value("file", file).Value("bytes", size).EndObject().ToString();
                        return t > until ? Error("the screenshot was not written (is the window minimized?)") : null;
                    }));
                    return null;
                }
                case "host":
                {
                    bool ok = _service.Host();
                    return Result(ok);
                }
                case "join":
                {
                    bool ok;
                    if (_service is DirectMultiplayerService direct)
                    {
                        string host = request.Arg("host", MultiplayerSettings.DefaultHostAddress);
                        int port = int.TryParse(request.Arg("port"), NumberStyles.None, CultureInfo.InvariantCulture, out int p) ? p : 0;
                        ok = port > 0 ? direct.Join(host, port) : direct.Join(0);
                    }
                    else
                    {
                        ok = ulong.TryParse(request.Arg("lobby"), NumberStyles.None, CultureInfo.InvariantCulture, out ulong lobby)
                            && _service.Join(lobby);
                    }
                    return Result(ok);
                }
                case "leave":
                    _service.Leave();
                    return Result(true);
                case "quit":
                {
                    _service.Leave();
                    bool ok = _runner != null && _runner.Submit("quit", null);
                    if (!ok)
                        Application.Quit();
                    return new JsonBuilder().BeginObject().Value("quitting", true).EndObject().ToString();
                }
                default:
                    return Error($"unknown path '/{request.Path}'; known: /status /cmd?line= /screenshot?name= /host /join?host=&port= /leave /quit");
            }
        }

        private string Result(bool ok) =>
            new JsonBuilder().BeginObject().Value("ok", ok).Value("state", _service.State.ToString())
                .Value("status", _service.StatusMessage).EndObject().ToString();

        private string Status()
        {
            var json = new JsonBuilder().BeginObject()
                .Value("mod", ProtocolInfo.ModVersion)
                .Value("port", Port)
                .Value("pid", System.Diagnostics.Process.GetCurrentProcess().Id)
                .Value("service", _service.GetType().Name)
                .Value("state", _service.State.ToString())
                .Value("isHost", _service.IsHost)
                .Value("status", _service.StatusMessage)
                .Value("unavailable", _service.UnavailableReason);
            if (_service is DirectMultiplayerService direct)
                json.Value("endpoint", direct.Endpoint).Value("bound", direct.BoundEndpoint);
            json.Value("localId", _service.Net != null ? _service.Net.LocalId.ToString() : string.Empty);
            json.BeginArray("players");
            foreach (var player in _service.Players)
            {
                json.BeginObject().Id("id", player.Id.Value).Value("name", player.DisplayName).Value("host", player.IsHost)
                    .Value("local", player.IsLocal).EndObject();
            }
            json.EndArray();
            json.Value("unityScene", SceneManager.GetActiveScene().name)
                .Value("focused", Application.isFocused)
                .Value("saveDir", Game.SaveDirOverride.Effective)
                .Value("persistentDataPath", Application.persistentDataPath)
                .Value("scene", _runner != null ? _runner.RunNow("scene") : string.Empty);
            return json.EndObject().ToString();
        }

        private static string Error(string message) =>
            new JsonBuilder().BeginObject().Value("error", message).EndObject().ToString();

        private static string Sanitize(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name ?? string.Empty)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.')
                    sb.Append(c);
            }
            return sb.Length == 0 ? "dev" : sb.ToString();
        }

        private static void Reply(Request request, string body)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: "
                    + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n");
                var stream = request.Client.GetStream();
                stream.Write(head, 0, head.Length);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (Exception e)
            {
                NetLog.Debug($"[DevControl] Reply failed: {e.Message}");
            }
            finally
            {
                try
                {
                    request.Client.Close();
                }
                catch (Exception)
                {
                    // Already closed.
                }
            }
        }

        private void OnDestroy()
        {
            _running = false;
            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // Stopping a listener that failed is not worth reporting.
            }
            while (_requests.TryDequeue(out var request))
                request.Client.Close();
            foreach (var job in _jobs)
                job.Request.Client.Close();
            _jobs.Clear();
        }

        private sealed class Request
        {
            public TcpClient Client;
            public string Path;
            public Dictionary<string, string> Query;

            public string Arg(string key, string fallback = "") => Query.TryGetValue(key, out var value) ? value : fallback;
        }

        /// <summary>An answer that takes several frames: <see cref="Step"/> returns the body once done, null until then.</summary>
        private sealed class Job
        {
            public Job(Request request, Func<double, string> step)
            {
                Request = request;
                Step = step;
            }

            public Request Request { get; }

            public Func<double, string> Step { get; }
        }
    }
}
