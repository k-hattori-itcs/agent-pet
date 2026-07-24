using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AgentCompanion.Services;

public record ProxyTarget(string Name, string Prefix, string Host);

public class ProxyServer
{
    private static readonly JsonSerializerOptions TargetJsonOptions = new() { WriteIndented = true };
    private readonly string _targetsPath;

    private TcpListener? _listener;
    private readonly List<ProxyClient> _clients = new();
    private readonly object _lock = new();
    private const int MaxClients = 8;
    private volatile bool _active;
    private int _port = 11435;
    private List<ProxyTarget> _targets;

    private static readonly object DebugLogSync = new();
    private const long MaxDebugLogBytes = 2 * 1024 * 1024;
    private static readonly string LogPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "pet_data", "debug.log");

    public ProxyServer()
        : this(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pet_data", "proxy_targets.json"))
    {
    }

    internal ProxyServer(string targetsPath)
    {
        _targetsPath = Path.GetFullPath(targetsPath);
        _targets = LoadTargets(_targetsPath);
    }
    public static bool EnableDebugLog { get; set; }

    internal static void Log(string msg)
    {
        if (!EnableDebugLog)
            return;
        try
        {
            lock (DebugLogSync)
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (dir != null)
                    Directory.CreateDirectory(dir);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= MaxDebugLogBytes)
                {
                    var backup = LogPath + ".1";
                    if (File.Exists(backup))
                        File.Delete(backup);
                    File.Move(LogPath, backup);
                }
                var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var sanitized = msg.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
                File.AppendAllText(LogPath, $"[{timestamp}] {sanitized}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break proxy traffic.
        }
    }
    internal static void LogException(string context, Exception ex)
    {
        Log($"[error] {context}: {ex.GetType().Name}: {ex.Message}");
    }

    public bool IsActive => _active;
    public int Port => _port;
    public IReadOnlyList<ProxyTarget> Targets
    {
        get
        {
            lock (_lock)
                return _targets.ToArray();
        }
    }

    public bool ReplaceTarget(int index, ProxyTarget target)
    {
        var normalized = NormalizeTarget(target);
        if (!IsValidTarget(normalized))
            return false;
        lock (_lock)
        {
            if (index < 0 || index >= _targets.Count ||
                _targets.Where((_, targetIndex) => targetIndex != index).Any(existing =>
                    existing.Prefix.Equals(normalized.Prefix, StringComparison.OrdinalIgnoreCase)))
                return false;
            _targets[index] = normalized;
        }
        SaveTargets();
        return true;
    }

    public bool AddTarget(ProxyTarget target)
    {
        var normalized = NormalizeTarget(target);
        if (!IsValidTarget(normalized))
            return false;
        lock (_lock)
        {
            if (_targets.Any(existing => existing.Prefix.Equals(normalized.Prefix, StringComparison.OrdinalIgnoreCase)))
                return false;
            _targets.Add(normalized);
        }
        SaveTargets();
        return true;
    }

    public bool RemoveTarget(int index)
    {
        lock (_lock)
        {
            if (index < 0 || index >= _targets.Count)
                return false;
            _targets.RemoveAt(index);
        }
        SaveTargets();
        return true;
    }

    public event Action<long, long, string>? TokenUsed;
    public event Action<string>? RequestReceived;
    public event Action<int, string>? ResponseFinished;

    public void Start(int port, IReadOnlyList<ProxyTarget> targets)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        var validatedTargets = targets.Select(NormalizeTarget).ToList();
        if (validatedTargets.Count == 0 || validatedTargets.Any(target => !IsValidTarget(target)))
            throw new InvalidDataException("Proxy target settings are invalid.");
        if (validatedTargets.Select(target => target.Prefix).Distinct(StringComparer.OrdinalIgnoreCase).Count() != validatedTargets.Count)
            throw new InvalidDataException("Proxy target prefixes must be unique.");

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        lock (_lock)
        {
            _port = port;
            _targets = validatedTargets;
            _listener = listener;
            _active = true;
        }
    }

    public void Stop()
    {
        _active = false;
        try
        {
            _listener?.Stop();
        }
        catch (Exception ex)
        {
            LogException("Stop listener", ex);
        }

        lock (_lock)
        {
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
        SaveTargets();
    }

    public void SaveTargets()
    {
        try
        {
            ProxyTarget[] snapshot;
            lock (_lock)
                snapshot = _targets.ToArray();
            var dir = Path.GetDirectoryName(_targetsPath);
            if (dir != null)
                Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(_targetsPath, JsonSerializer.Serialize(snapshot, TargetJsonOptions));
        }
        catch (Exception ex)
        {
            LogException("Save proxy targets", ex);
            AppLogger.Error("Proxy target save failed.", ex);
        }
    }

    private static List<ProxyTarget> LoadTargets(string targetsPath)
    {
        if (TryLoadTargets(targetsPath, out var targets) ||
            TryLoadTargets(targetsPath + ".bak", out targets))
            return targets;

        return new List<ProxyTarget>
        {
            new("OpenAI", "oai", "api.openai.com"),
        };
    }

    private static bool TryLoadTargets(string path, out List<ProxyTarget> targets)
    {
        targets = new List<ProxyTarget>();
        if (!File.Exists(path))
            return false;
        try
        {
            var loaded = JsonSerializer.Deserialize<List<ProxyTarget>>(File.ReadAllText(path));
            if (loaded == null)
                return false;
            var valid = loaded.Select(NormalizeTarget).Where(IsValidTarget).ToList();
            if (valid.Count == 0 || valid.Count != loaded.Count ||
                valid.Select(target => target.Prefix).Distinct(StringComparer.OrdinalIgnoreCase).Count() != valid.Count)
                return false;
            targets = valid;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogException("Load proxy targets", ex);
            return false;
        }
    }


    private static ProxyTarget NormalizeTarget(ProxyTarget? target)
    {
        return new ProxyTarget(
            target?.Name?.Trim() ?? string.Empty,
            target?.Prefix?.Trim() ?? string.Empty,
            target?.Host?.Trim().TrimEnd('.') ?? string.Empty);
    }

    private static bool IsValidTarget(ProxyTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.Name) || target.Name.Length > 64 ||
            string.IsNullOrWhiteSpace(target.Prefix) || target.Prefix.Length > 32 ||
            string.IsNullOrWhiteSpace(target.Host) || target.Host.Length > 253)
            return false;
        if (target.Prefix.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;
        if (Uri.CheckHostName(target.Host) != UriHostNameType.Dns ||
            target.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            target.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
    public void Poll()
    {
        if (!_active || _listener == null) return;

        while (_listener.Pending())
        {
            try
            {
                var client = _listener.AcceptTcpClient();
                ProxyTarget[] targetSnapshot;
                lock (_lock)
                {
                    if (_clients.Count >= MaxClients)
                    {
                        RejectBusyClient(client);
                        continue;
                    }
                    targetSnapshot = _targets.ToArray();
                    _clients.Add(new ProxyClient(client, targetSnapshot, OnToken, this));
                }
            }
            catch (Exception ex)
            {
                LogException("Accept proxy client", ex);
            }
        }

        lock (_lock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (!_clients[i].Poll() || _clients[i].Done)
                {
                    _clients[i].Dispose();
                    _clients.RemoveAt(i);
                }
            }

        }
    }


    private static void RejectBusyClient(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 503 Service Unavailable\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                stream.Write(response);
            }
        }
        catch (Exception ex)
        {
            LogException("Reject excess proxy client", ex);
        }
    }
    private void OnToken(long input, long output, string target)
    {
        TokenUsed?.Invoke(input, output, target);
    }

    private class ProxyClient : IDisposable
    {
        private const int MaxHeaderBytes = 32 * 1024;
        private const int MaxRequestBodyBytes = 4 * 1024 * 1024;
        private const int MaxResponseCaptureBytes = 1024 * 1024;
        private const int MaxDecompressedBytes = 4 * 1024 * 1024;
        private readonly TcpClient _client;
        private readonly NetworkStream _clientStream;
        private TcpClient? _target;
        private SslStream? _targetSsl;
        private readonly IReadOnlyList<ProxyTarget> _targets;
        private readonly Action<long, long, string> _onToken;
        private readonly ProxyServer _server;
        private readonly MemoryStream _requestBuffer = new();
        private readonly MemoryStream _responseBuffer = new();
        private string _matchedTarget = "";
        private int _state; // 0=reading request, 1=connecting, 2=forwarding
        private long _inputTokens;
        private long _outputTokens;
        private Task? _connectTask;
        private volatile bool _done;
        private int _responseStatus;
        private bool _responseCaptureOverflow;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly DateTime _acceptedAtUtc = DateTime.UtcNow;

        public bool Done => _done;

        public ProxyClient(TcpClient client, IReadOnlyList<ProxyTarget> targets, Action<long, long, string> onToken, ProxyServer server)
        {
            _client = client;
            _client.ReceiveTimeout = 30_000;
            _client.SendTimeout = 30_000;
            _client.NoDelay = true;
            _clientStream = client.GetStream();
            _targets = targets;
            _onToken = onToken;
            _server = server;
        }

        public bool Poll()
        {
            if (_done) return false;
            try
            {
                return _state switch { 0 => ReadRequest(), 1 => ConnectAndForward(), _ => true };
            }
            catch (Exception ex)
            {
                ProxyServer.LogException("Poll proxy client", ex);
                _done = true;
                return false;
            }
        }

        private bool ReadRequest()
        {
            if (DateTime.UtcNow - _acceptedAtUtc > TimeSpan.FromSeconds(30))
                return RejectRequest(408, "Request Timeout");
            if (_client.Available == 0)
                return true;

            var buffer = new byte[Math.Min(8192, _client.Available)];
            var read = _clientStream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                return false;
            _requestBuffer.Write(buffer, 0, read);
            if (_requestBuffer.Length > MaxHeaderBytes + MaxRequestBodyBytes)
                return RejectRequest(413, "Payload Too Large");

            var data = _requestBuffer.GetBuffer();
            var length = (int)_requestBuffer.Length;
            var headerEnd = FindBytes(data, length, "\r\n\r\n"u8);
            if (headerEnd < 0)
                return length <= MaxHeaderBytes || RejectRequest(431, "Request Header Fields Too Large");
            if (headerEnd > MaxHeaderBytes)
                return RejectRequest(431, "Request Header Fields Too Large");

            var headers = Encoding.ASCII.GetString(data, 0, headerEnd);
            if (HasAmbiguousHeaders(headers))
                return RejectRequest(400, "Ambiguous HTTP Headers");
            if (!IsAllowedLocalRequest(headers))
                return RejectRequest(403, "Forbidden");
            if (TryGetHeader(headers, "Transfer-Encoding", out _))
                return RejectRequest(400, "Transfer-Encoding Is Not Supported");

            var contentLength = 0;
            if (TryGetHeader(headers, "Content-Length", out var contentLengthText) &&
                (!int.TryParse(contentLengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) ||
                 contentLength < 0 || contentLength > MaxRequestBodyBytes))
                return RejectRequest(413, "Invalid Content-Length");

            var bodyStart = headerEnd + 4;
            var expectedLength = bodyStart + contentLength;
            if (length < expectedLength)
                return true;
            if (length != expectedLength)
                return RejectRequest(400, "HTTP Pipelining Is Not Supported");

            _matchedTarget = GetProxyPrefix(headers);
            if (!_targets.Any(target => target.Prefix.Equals(_matchedTarget, StringComparison.OrdinalIgnoreCase)))
                return RejectRequest(421, "Unknown Proxy Prefix");

            var bodyText = contentLength > 0
                ? Encoding.UTF8.GetString(data, bodyStart, contentLength)
                : string.Empty;
            var isStream = IsStreamingBody(bodyText);
            ProxyServer.Log($"[req] {(isStream ? "SSE" : "REST")} prefix={SanitizeForLog(_matchedTarget)}");
            _server.RequestReceived?.Invoke(_matchedTarget);
            _state = 1;
            return true;
        }

        private bool RejectRequest(int statusCode, string reason)
        {
            try
            {
                _responseStatus = statusCode;
                var payload = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {statusCode} {reason}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                _clientStream.Write(payload, 0, payload.Length);
                _clientStream.Flush();
            }
            catch (Exception ex)
            {
                ProxyServer.LogException("Reject proxy request", ex);
            }
            _server.ResponseFinished?.Invoke(statusCode, _matchedTarget);
            _done = true;
            return false;
        }

        private static bool HasAmbiguousHeaders(string headers)
        {
            return CountHeader(headers, "Host") != 1 ||
                   CountHeader(headers, "Content-Length") > 1 ||
                   CountHeader(headers, "Transfer-Encoding") > 1 ||
                   CountHeader(headers, "Origin") > 1;
        }

        private static int CountHeader(string headers, string name)
        {
            var count = 0;
            foreach (var line in headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var colon = line.IndexOf(':');
                if (colon > 0 && string.Equals(line[..colon].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }
        private static bool IsAllowedLocalRequest(string headers)
        {
            if (TryGetHeader(headers, "Host", out var host))
            {
                var authority = host.Split(':', 2)[0].Trim('[', ']');
                if (!authority.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
                    !authority.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                    !authority.Equals("::1", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!TryGetHeader(headers, "Origin", out var origin) || string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
                return true;
            return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
                   (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
        }
        private bool ConnectAndForward()
        {
            if (_connectTask == null)
            {
                var requestedPrefix = _matchedTarget;
                var target = _targets.FirstOrDefault(item =>
                    item.Prefix.Equals(requestedPrefix, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                    return RejectRequest(421, "Unknown Proxy Prefix");
                _matchedTarget = target.Name;
                _connectTask = ConnectAndSendAsync(target, requestedPrefix);
                return true;
            }
            if (!_connectTask.IsCompleted)
                return true;
            if (_connectTask.IsFaulted || _connectTask.IsCanceled)
            {
                if (_connectTask.Exception != null)
                    ProxyServer.LogException("Connect upstream", _connectTask.Exception.GetBaseException());
                return RejectRequest(502, "Bad Gateway");
            }

            _connectTask = null;
            _state = 2;
            _ = ForwardRawAsync();
            return true;
        }
        private async Task ConnectAndSendAsync(ProxyTarget target, string requestedPrefix)
        {
            var addresses = await Dns.GetHostAddressesAsync(target.Host, _lifetime.Token)
                .WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token)
                .ConfigureAwait(false);
            var address = addresses.FirstOrDefault(IsPublicAddress)
                ?? throw new InvalidDataException("The upstream host did not resolve to a public address.");

            _target = new TcpClient(address.AddressFamily)
            {
                ReceiveTimeout = 300_000,
                SendTimeout = 30_000,
                NoDelay = true
            };
            await _target.ConnectAsync(address, 443, _lifetime.Token)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token)
                .ConfigureAwait(false);
            _targetSsl = new SslStream(_target.GetStream(), false);
            await _targetSsl.AuthenticateAsClientAsync(target.Host)
                .WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token)
                .ConfigureAwait(false);

            var raw = _requestBuffer.ToArray();
            var headerEnd = FindBytes(raw, raw.Length, "\r\n\r\n"u8);
            if (headerEnd < 0)
                throw new InvalidDataException("The request header was incomplete.");

            var headers = Encoding.ASCII.GetString(raw, 0, headerEnd);
            var bodyStart = headerEnd + 4;
            var bodyBytes = raw[bodyStart..];
            headers = RewriteRequestHeaders(headers, target, requestedPrefix);
            headers = SetHeader(headers, "Connection", "close");

            if (bodyBytes.Length > 0)
            {
                var bodyText = Encoding.UTF8.GetString(bodyBytes);
                if (IsStreamingBody(bodyText) && !HasJsonProperty(bodyText, "stream_options"))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(bodyText);
                        var newBody = InjectIncludeUsage(document.RootElement);
                        bodyBytes = JsonSerializer.SerializeToUtf8Bytes(newBody);
                        headers = SetHeader(headers, "Content-Length", bodyBytes.Length.ToString(CultureInfo.InvariantCulture));
                    }
                    catch (JsonException ex)
                    {
                        ProxyServer.LogException("Inject stream usage option", ex);
                    }
                }
            }

            var headerBytes = Encoding.ASCII.GetBytes(headers + "\r\n\r\n");
            await _targetSsl.WriteAsync(headerBytes, _lifetime.Token).ConfigureAwait(false);
            if (bodyBytes.Length > 0)
                await _targetSsl.WriteAsync(bodyBytes, _lifetime.Token).ConfigureAwait(false);
            await _targetSsl.FlushAsync(_lifetime.Token).ConfigureAwait(false);
            _requestBuffer.SetLength(0);
            _requestBuffer.Capacity = 0;
        }

        private static bool IsPublicAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address))
                return false;
            if (address.IsIPv4MappedToIPv6)
                return IsPublicAddress(address.MapToIPv4());
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = address.GetAddressBytes();
                return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127 &&
                       !(bytes[0] == 169 && bytes[1] == 254) &&
                       !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                       !(bytes[0] == 192 && bytes[1] == 168) &&
                       !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                       bytes[0] < 224;
            }
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
                return !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None) &&
                       !address.IsIPv6Multicast && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal &&
                       (address.GetAddressBytes()[0] & 0xFE) != 0xFC;
            return false;
        }
        private async Task ForwardRawAsync()
        {
            try
            {
                if (_targetSsl == null)
                    return;
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await _targetSsl.ReadAsync(buffer, _lifetime.Token)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromMinutes(5), _lifetime.Token)
                        .ConfigureAwait(false);
                    if (read == 0)
                        break;

                    if (!_responseCaptureOverflow)
                    {
                        if (_responseBuffer.Length + read <= MaxResponseCaptureBytes)
                        {
                            _responseBuffer.Write(buffer, 0, read);
                            if (_responseStatus == 0)
                            {
                                var captured = _responseBuffer.GetBuffer();
                                var capturedLength = (int)_responseBuffer.Length;
                                var headerEnd = FindBytes(captured, capturedLength, "\r\n\r\n"u8);
                                if (headerEnd >= 0)
                                {
                                    _responseStatus = ParseHttpStatus(captured, headerEnd);
                                    ProxyServer.Log($"[rsp] {_responseStatus} {SanitizeForLog(_matchedTarget)}");
                                }
                            }
                        }
                        else
                        {
                            _responseCaptureOverflow = true;
                            _responseBuffer.SetLength(0);
                            _responseBuffer.Capacity = 0;
                            ProxyServer.Log("[warn] response capture limit exceeded");
                        }
                    }

                    await _clientStream.WriteAsync(buffer.AsMemory(0, read), _lifetime.Token).ConfigureAwait(false);
                    await _clientStream.FlushAsync(_lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _responseStatus = 502;
                ProxyServer.LogException("Forward upstream response", ex);
            }
            finally
            {
                if (_responseStatus == 0)
                    _responseStatus = 502;
                _server.ResponseFinished?.Invoke(_responseStatus, _matchedTarget);
                FinalizeToken();
                try
                {
                    if (_client.Client != null)
                        _client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (Exception ex)
                {
                    ProxyServer.LogException("Shutdown client socket", ex);
                }
                _done = true;
            }
        }
        private static int ParseHttpStatus(byte[] buf, int len)
        {
            var text = Encoding.ASCII.GetString(buf, 0, Math.Min(len, 100));
            var firstLine = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
        }

        private void FinalizeToken()
        {
            if (!_responseCaptureOverflow && _responseBuffer.Length > 0) ParseUsage(_responseBuffer.ToArray());
            if (_inputTokens > 0 || _outputTokens > 0)
            {
                ProxyServer.Log($"[token] {_matchedTarget} in={_inputTokens} out={_outputTokens}");
                _onToken(_inputTokens, _outputTokens, _matchedTarget);
            }
        }

        private void ParseUsage(byte[] data)
        {
            try
            {
                int sepIdx = FindBytes(data, data.Length, "\r\n\r\n"u8);
                var sepLen = 4;
                if (sepIdx == -1)
                {
                    sepIdx = FindBytes(data, data.Length, "\n\n"u8);
                    sepLen = 2;
                }
                if (sepIdx == -1) return;

                var hdrText = Encoding.ASCII.GetString(data, 0, sepIdx);
                bool isChunked = TryGetHeader(hdrText, "Transfer-Encoding", out var transferEncoding)
                    && transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
                bool isGzip = TryGetHeader(hdrText, "Content-Encoding", out var contentEncoding)
                    && contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);

                int bs = sepIdx + sepLen;
                var body = new byte[data.Length - bs];
                Buffer.BlockCopy(data, bs, body, 0, body.Length);

                if (isChunked) body = Dechunk(body);
                if (isGzip)
                {
                    try
                    {
                        using var ms = new MemoryStream(body);
                        using var gz = new GZipStream(ms, CompressionMode.Decompress);
                        using var rs = new MemoryStream();
                        CopyWithLimit(gz, rs, MaxDecompressedBytes);
                        body = rs.ToArray();
                    }
                    catch (Exception ex)
                    {
                        ProxyServer.LogException("Decompress gzip response", ex);
                    }
                }

                var text = Encoding.UTF8.GetString(body);

                foreach (var line in text.Split('\n'))
                {
                    var t = line.Trim();
                    string js;
                    if (t.StartsWith("data: ", StringComparison.Ordinal)) js = t[6..];
                    else if (t.StartsWith("data:", StringComparison.Ordinal)) js = t[5..];
                    else continue;
                    if (js == "[DONE]") continue;
                    TryFindUsage(js, "Parse SSE usage event");
                }

                if (_inputTokens == 0 && _outputTokens == 0 && text.TrimStart().StartsWith('{'))
                    TryFindUsage(text.Trim(), "Parse JSON usage body");
            }
            catch (Exception ex)
            {
                ProxyServer.LogException("Parse usage", ex);
            }
        }

        private void TryFindUsage(string json, string context)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                FindUsage(doc.RootElement);
            }
            catch (Exception ex)
            {
                ProxyServer.LogException(context, ex);
            }
        }

        private void FindUsage(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object) return;

            if (element.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                foreach (var u in usage.EnumerateObject())
                {
                    if (u.Name is "prompt_tokens" or "input_tokens")
                    {
                        if (long.TryParse(u.Value.GetRawText(), out var v) && v > 0)
                            _inputTokens = Math.Max(_inputTokens, v);
                    }
                    else if (u.Name is "completion_tokens" or "output_tokens")
                    {
                        if (long.TryParse(u.Value.GetRawText(), out var v) && v > 0)
                            _outputTokens = Math.Max(_outputTokens, v);
                    }
                }
            }

            if (element.TryGetProperty("usageMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                foreach (var u in meta.EnumerateObject())
                {
                    if (u.Name is "promptTokenCount" or "prompt_token_count")
                    {
                        if (long.TryParse(u.Value.GetRawText(), out var v) && v > 0)
                            _inputTokens = Math.Max(_inputTokens, v);
                    }
                    else if (u.Name is "candidatesTokenCount" or "candidates_token_count")
                    {
                        if (long.TryParse(u.Value.GetRawText(), out var v) && v > 0)
                            _outputTokens = Math.Max(_outputTokens, v);
                    }
                }
            }

            foreach (var p in element.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object)
                    FindUsage(p.Value);
                else if (p.Value.ValueKind == JsonValueKind.Array)
                    foreach (var item in p.Value.EnumerateArray())
                        FindUsage(item);
            }
        }

        private static byte[] Dechunk(byte[] data) =>
            ProxyProtocol.Dechunk(data, MaxDecompressedBytes);

        private static int FindBytes(byte[] data, int length, ReadOnlySpan<byte> pattern)
        {
            for (int i = 0; i <= length - pattern.Length; i++)
            {
                bool m = true;
                for (int j = 0; j < pattern.Length; j++) if (data[i + j] != pattern[j]) { m = false; break; }
                if (m) return i;
            }
            return -1;
        }

        private static string GetProxyPrefix(string headers)
        {
            var firstLine = headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return "";

            var path = parts[1];
            if (!path.StartsWith('/')) return "";
            var withoutSlash = path[1..];
            var end = withoutSlash.IndexOfAny(new[] { '/', '?' });
            return end >= 0 ? withoutSlash[..end] : withoutSlash;
        }

        private static bool IsStreamingBody(string body) => ProxyProtocol.IsStreamingBody(body);

        private static bool HasJsonProperty(string body, string propertyName)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.ValueKind == JsonValueKind.Object &&
                       document.RootElement.TryGetProperty(propertyName, out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string SanitizeForLog(string value)
        {
            return value.Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", "", StringComparison.Ordinal);
        }

        private static void CopyWithLimit(Stream source, Stream destination, int maxBytes)
        {
            var buffer = new byte[8192];
            var total = 0;
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    return;
                total = checked(total + read);
                if (total > maxBytes)
                    throw new InvalidDataException("Decompressed response exceeded the limit.");
                destination.Write(buffer, 0, read);
            }
        }
        private static bool TryGetHeader(string headers, string name, out string value)
        {
            foreach (var line in headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (string.Equals(line[..colon].Trim(), name, StringComparison.OrdinalIgnoreCase))
                {
                    value = line[(colon + 1)..].Trim();
                    return true;
                }
            }

            value = "";
            return false;
        }

        private static bool TryGetContentLength(string headers, out int contentLength)
        {
            contentLength = 0;
            return TryGetHeader(headers, "Content-Length", out var value)
                && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength);
        }

        private static string RewriteRequestHeaders(string headers, ProxyTarget target, string requestedPrefix)
        {
            var lines = headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            if (lines.Count > 0)
                lines[0] = RewriteRequestLine(lines[0], requestedPrefix);

            return SetHeader(string.Join("\r\n", lines), "Host", target.Host);
        }

        private static string RewriteRequestLine(string line, string requestedPrefix)
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(requestedPrefix)) return line;

            var path = parts[1];
            var prefixPath = "/" + requestedPrefix;
            if (path.Equals(prefixPath, StringComparison.OrdinalIgnoreCase))
                path = "/";
            else if (path.StartsWith(prefixPath + "/", StringComparison.OrdinalIgnoreCase))
                path = path[prefixPath.Length..];
            else if (path.StartsWith(prefixPath + "?", StringComparison.OrdinalIgnoreCase))
                path = "/" + path[prefixPath.Length..];

            return $"{parts[0]} {path} {parts[2]}";
        }

        private static string SetHeader(string headers, string name, string value)
        {
            var lines = headers.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            for (var i = 1; i < lines.Count; i++)
            {
                var colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                if (!string.Equals(lines[i][..colon].Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                lines[i] = $"{name}: {value}";
                return string.Join("\r\n", lines);
            }

            lines.Add($"{name}: {value}");
            return string.Join("\r\n", lines);
        }

        private static JsonElement InjectIncludeUsage(JsonElement root)
        {
            using var ms = new MemoryStream();
            using var w = new Utf8JsonWriter(ms);
            w.WriteStartObject();
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name == "stream_options")
                {
                    w.WriteStartObject("stream_options");
                    w.WriteBoolean("include_usage", true);
                    foreach (var sp in p.Value.EnumerateObject())
                        if (sp.Name != "include_usage") sp.WriteTo(w);
                    w.WriteEndObject();
                }
                else p.WriteTo(w);
            }
            if (!root.TryGetProperty("stream_options", out _) && root.TryGetProperty("stream", out var sv) && sv.GetBoolean())
            {
                w.WriteStartObject("stream_options");
                w.WriteBoolean("include_usage", true);
                w.WriteEndObject();
            }
            w.WriteEndObject(); w.Flush(); ms.Position = 0;
            using var doc = JsonDocument.Parse(ms);
            return doc.RootElement.Clone();
        }

        public void Dispose()
        {
            _lifetime.Cancel();
            try
            {
                _targetSsl?.Close();
            }
            catch (Exception ex)
            {
                ProxyServer.LogException("Close upstream TLS stream", ex);
            }

            try
            {
                _targetSsl?.Dispose();
            }
            catch (Exception ex)
            {
                ProxyServer.LogException("Dispose upstream TLS stream", ex);
            }

            try
            {
                _client.Close();
            }
            catch (Exception ex)
            {
                ProxyServer.LogException("Close client", ex);
            }

            try
            {
                _target?.Close();
            }
            catch (Exception ex)
            {
                ProxyServer.LogException("Close upstream", ex);
            }

            _client.Dispose();
            _target?.Dispose();
            _requestBuffer.Dispose();
            _responseBuffer.Dispose();
            _lifetime.Dispose();
        }
    }
}
