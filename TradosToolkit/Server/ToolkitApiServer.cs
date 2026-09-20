using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TradosToolkit.Server
{
    /// <summary>
    /// Studio 打开即自启的本地 HTTP 服务：供 Claude Code 等 agent 通过 API
    /// 驱动项目创建/预翻译/分析/打包。配置与令牌放 %AppData%\TradosToolkit\。
    /// 仅监听 localhost；除 /api/status 外所有请求要求 X-Api-Key 匹配令牌。
    /// </summary>
    public sealed class ToolkitApiServer
    {
        private static readonly ToolkitApiServer _instance = new ToolkitApiServer();

        public static ToolkitApiServer Instance => _instance;

        private int _started;
        private HttpListener _listener;

        public string Token { get; private set; }
        public int Port { get; private set; } = ApiConfig.DefaultPort;
        public DateTime StartedAt { get; private set; }
        public bool IsListening => _listener != null && _listener.IsListening;

        /// <summary>幂等：Ribbon 组构造时调用。</summary>
        public void EnsureStarted()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
                return;

            try
            {
                var config = ApiConfig.Load();
                if (!config.Enabled)
                {
                    ApiLog.Write("api disabled by config");
                    return;
                }

                Port = config.Port;
                Token = config.GetOrCreateToken();
                StartedAt = DateTime.Now;

                _listener = new HttpListener();
                _listener.Prefixes.Add("http://localhost:" + Port + "/");
                _listener.Start();

                var thread = new Thread(ListenLoop) { IsBackground = true, Name = "TradosToolkitApi" };
                thread.Start();
                ApiLog.Write("listening on http://localhost:" + Port + "/");
            }
            catch (Exception e)
            {
                Interlocked.Exchange(ref _started, 0);
                ApiLog.Write("start failed: " + e.Message);
            }
        }

        private void ListenLoop()
        {
            while (_listener != null && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = _listener.GetContext();
                }
                catch (Exception)
                {
                    break;
                }

                ThreadPool.QueueUserWorkItem(_ => Handle(context));
            }
        }

        private void Handle(HttpListenerContext context)
        {
            ApiResult result;
            var request = context.Request;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var body = ReadBody(request);
                result = ProjectApi.Handle(
                    request.HttpMethod,
                    request.Url.AbsolutePath,
                    ParseQuery(request),
                    body,
                    request.Headers["X-Api-Key"] ?? request.QueryString["key"],
                    Token);
            }
            catch (Exception e)
            {
                ApiLog.Write("error " + request.Url.AbsolutePath + ": " + e);
                result = ApiResult.Json(500, new Dictionary<string, object> { { "error", e.Message } });
            }
            finally
            {
                watch.Stop();
            }

            // 状态面板自动刷新会刷屏，不进请求轨迹
            if (request.Url.AbsolutePath != "/" && request.Url.AbsolutePath != "/status.html")
                RequestTracker.Record(request.HttpMethod, request.Url.AbsolutePath, result.Status, watch.ElapsedMilliseconds);

            try
            {
                Write(context.Response, result);
            }
            catch (Exception e)
            {
                ApiLog.Write("write failed: " + e.Message);
            }
        }

        private static string ReadBody(HttpListenerRequest request)
        {
            if (!request.HasEntityBody)
                return null;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                return reader.ReadToEnd();
        }

        private static Dictionary<string, string> ParseQuery(HttpListenerRequest request)
        {
            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (request.Url.Query.Length <= 1)
                return query;
            foreach (var part in request.Url.Query.Substring(1).Split('&'))
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                query[part.Substring(0, idx)] = Uri.UnescapeDataString(part.Substring(idx + 1));
            }
            return query;
        }

        private static void Write(HttpListenerResponse response, ApiResult result)
        {
            response.StatusCode = result.Status;
            byte[] bytes;
            if (result.Bytes != null)
            {
                bytes = result.Bytes;
                response.ContentType = result.ContentType ?? "application/octet-stream";
                if (!string.IsNullOrEmpty(result.DownloadName))
                    response.AddHeader("Content-Disposition", "attachment; filename=" + result.DownloadName);
            }
            else
            {
                var json = result.Payload == null
                    ? "null"
                    : new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(result.Payload);
                bytes = Encoding.UTF8.GetBytes(json);
                response.ContentType = "application/json; charset=utf-8";
            }
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.OutputStream.Close();
        }
    }

    /// <summary>最近请求环形轨迹（不含状态面板自身的刷新），供 HTML 面板与 /api/requests 展示。</summary>
    public static class RequestTracker
    {
        private const int Cap = 100;
        private static readonly object Gate = new object();
        private static readonly Queue<Dictionary<string, object>> Ring = new Queue<Dictionary<string, object>>();
        private static long _total;

        public static long Total => Interlocked.Read(ref _total);

        public static void Record(string method, string path, int status, long ms)
        {
            Interlocked.Increment(ref _total);
            var entry = new Dictionary<string, object>
            {
                { "time", DateTime.Now.ToString("HH:mm:ss") },
                { "method", method },
                { "path", path },
                { "status", status },
                { "ms", ms },
            };
            lock (Gate)
            {
                Ring.Enqueue(entry);
                while (Ring.Count > Cap) Ring.Dequeue();
            }
        }

        public static List<Dictionary<string, object>> Snapshot()
        {
            lock (Gate) return Ring.Reverse().ToList();
        }
    }

    public class ApiResult
    {
        public int Status;
        public object Payload;
        public byte[] Bytes;
        public string ContentType;
        public string DownloadName;

        public static ApiResult Json(int status, object payload)
        {
            return new ApiResult { Status = status, Payload = payload };
        }

        public static ApiResult File(byte[] bytes, string contentType, string downloadName)
        {
            return new ApiResult { Status = 200, Bytes = bytes, ContentType = contentType, DownloadName = downloadName };
        }
    }

    public class ApiConfig
    {
        public const int DefaultPort = 53902;

        public bool Enabled = true;
        public int Port = DefaultPort;

        public static string ConfigFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TradosToolkit");

        public static ApiConfig Load()
        {
            var config = new ApiConfig();
            try
            {
                var file = Path.Combine(ConfigFolder, "api.json");
                if (!File.Exists(file))
                {
                    Directory.CreateDirectory(ConfigFolder);
                    File.WriteAllText(file, "{\"enabled\":true,\"port\":" + DefaultPort + "}");
                    return config;
                }
                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(file));
                if (json == null) return config;
                if (json.TryGetValue("enabled", out var enabled) && enabled is bool b) config.Enabled = b;
                if (json.TryGetValue("port", out var port)) config.Port = Convert.ToInt32(port);
            }
            catch (Exception e)
            {
                ApiLog.Write("config load failed: " + e.Message);
            }
            return config;
        }

        public string GetOrCreateToken()
        {
            var file = Path.Combine(ConfigFolder, "api.token");
            try
            {
                Directory.CreateDirectory(ConfigFolder);
                if (File.Exists(file))
                {
                    var token = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrEmpty(token))
                        return token;
                }
            }
            catch (Exception e)
            {
                ApiLog.Write("token read failed: " + e.Message);
            }

            var newToken = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, newToken);
            return newToken;
        }
    }

    public static class ApiLog
    {
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                    File.AppendAllText(
                        Path.Combine(ApiConfig.ConfigFolder, "api.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
