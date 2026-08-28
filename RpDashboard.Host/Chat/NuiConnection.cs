using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;

namespace RpDashboard.Host.Chat;

/// <summary>
/// The one DevTools connection to FiveM's NUI, for the whole process.
///
/// This is the merge of three near-identical readers — Unit Alert's and RP Pace's were the
/// same file with the namespace changed, Street Medic's was the same handshake with a richer
/// expression. The handshake below is theirs, unchanged, because it is proven:
///
///   GET http://127.0.0.1:13172/json    -> target list
///   target with url nui://game/ui/root.html
///   -> WebSocket to its webSocketDebuggerUrl
///   -> Page.getFrameTree, find the child frame at https://cfx-nui-client/...
///      (GTAW's HUD resource is named "client", so its NUI origin is cfx-nui-client)
///   -> Page.createIsolatedWorld on that frame
///   -> Runtime.evaluate: document.querySelectorAll('.chat__messages > li')
///
/// What changed is how often it runs. Three processes each evaluated against the HUD every
/// 500 ms; this runs once per poll for all of them, and ChatFeed only polls at all while a
/// tool is actually listening.
///
/// Read-only, loopback-only. Nothing is ever sent into the game.
/// </summary>
internal sealed class NuiConnection : IAsyncDisposable
{
    private static readonly Uri TargetsUri = new("http://127.0.0.1:13172/json");
    private const string RootUiUrl = "nui://game/ui/root.html";
    private const string ClientFramePrefix = "https://cfx-nui-client/";

    /// <summary>How many messages off the end of the HUD buffer each poll pulls back.</summary>
    private const int TailLines = 120;

    /// <summary>
    /// outerHTML rather than innerText, and one array rather than two.
    ///
    /// Street Medic needs the span's inline colour — that is how GTAW distinguishes /me, /do,
    /// OOC and speech, and it classifies more reliably than the text does. Unit Alert and RP
    /// Pace only need the text, but stripping the tags here in C# is cheaper than asking the
    /// game to build and serialise a second array of innerText on every poll. So the game does
    /// one pass and sends one payload; the host derives the plain form.
    ///
    /// The slice bounds that payload: the HUD keeps far more scrollback than any tool looks at,
    /// and anything older than the tail was either already delivered or predates the app.
    /// </summary>
    private const string ReadChatExpression =
        "JSON.stringify(Array.from(document.querySelectorAll('.chat__messages > li'))" +
        ".slice(-120).map(function(el){return el.outerHTML;}))";

    private readonly HttpClient _http;
    private readonly byte[] _receiveBuffer = new byte[64 * 1024];

    private ClientWebSocket? _socket;
    private int _contextId;
    private int _requestId;

    public bool IsConnected => _socket?.State == WebSocketState.Open && _contextId != 0;

    public NuiConnection()
    {
        var handler = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
    }

    /// <summary>Every chat message currently in the tail of the HUD's list, as raw outerHTML.</summary>
    public async Task<IReadOnlyList<string>> ReadVisibleHtmlAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken);

        string? json = await EvaluateAsync(ReadChatExpression, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        string[] lines = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        return lines.Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
    }

    public async Task ResetAsync()
    {
        if (_socket is not null)
        {
            try
            {
                if (_socket.State == WebSocketState.Open)
                {
                    await _socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "reset",
                        CancellationToken.None);
                }
            }
            catch
            {
                // The game closing mid-call is the normal way this ends.
            }

            _socket.Dispose();
            _socket = null;
        }

        _contextId = 0;
        _requestId = 0;
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return;
        await ResetAsync();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(2));

        string targetJson = await _http.GetStringAsync(TargetsUri, linked.Token);
        using JsonDocument targets = JsonDocument.Parse(targetJson);

        string? debuggerUrl = null;
        foreach (JsonElement target in targets.RootElement.EnumerateArray())
        {
            if (!target.TryGetProperty("url", out JsonElement url) ||
                !string.Equals(url.GetString(), RootUiUrl, StringComparison.OrdinalIgnoreCase))
                continue;

            if (target.TryGetProperty("webSocketDebuggerUrl", out JsonElement ws))
                debuggerUrl = ws.GetString();
            break;
        }

        if (string.IsNullOrWhiteSpace(debuggerUrl))
            throw new IOException("FiveM's root NUI page isn't available yet.");

        var socketUri = new Uri(debuggerUrl);
        if (!IsLoopback(socketUri))
            throw new IOException("Refusing a DevTools endpoint that isn't on this machine.");

        _socket = new ClientWebSocket();
        _socket.Options.Proxy = null;
        await _socket.ConnectAsync(socketUri, linked.Token);

        JsonElement frameTree = await RequestAsync("Page.getFrameTree", new { }, linked.Token);
        string? frameId = FindClientFrameId(frameTree);
        if (string.IsNullOrWhiteSpace(frameId))
            throw new IOException("The GTA World HUD hasn't loaded yet.");

        JsonElement world = await RequestAsync("Page.createIsolatedWorld", new
        {
            frameId,
            worldName = "rp-dashboard-reader",
            // Note the missing 's': the typo is in the DevTools Protocol itself.
            grantUniveralAccess = false
        }, linked.Token);

        if (!world.TryGetProperty("executionContextId", out JsonElement context))
            throw new IOException("Couldn't open a script context on the HUD frame.");

        _contextId = context.GetInt32();
    }

    private async Task<string?> EvaluateAsync(string expression, CancellationToken cancellationToken)
    {
        JsonElement result = await RequestAsync("Runtime.evaluate", new
        {
            expression,
            contextId = _contextId,
            returnByValue = true
        }, cancellationToken);

        if (!result.TryGetProperty("result", out JsonElement inner) ||
            !inner.TryGetProperty("value", out JsonElement value))
            return null;

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private async Task<JsonElement> RequestAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        if (_socket is null || _socket.State != WebSocketState.Open)
            throw new IOException("Not connected to FiveM's DevTools.");

        int id = Interlocked.Increment(ref _requestId);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(TimeSpan.FromSeconds(2));
        await _socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, true, linked.Token);

        while (true)
        {
            JsonElement response = await ReceiveAsync(linked.Token);

            // The socket also carries unsolicited CDP events; skip anything that
            // isn't the reply to the request we just sent.
            if (!response.TryGetProperty("id", out JsonElement responseId) ||
                responseId.GetInt32() != id)
                continue;

            if (response.TryGetProperty("error", out _))
                throw new IOException($"FiveM's DevTools rejected {method}.");

            return response.TryGetProperty("result", out JsonElement result)
                ? result.Clone()
                : default;
        }
    }

    private async Task<JsonElement> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_socket is null) throw new IOException("Not connected to FiveM's DevTools.");

        ValueWebSocketReceiveResult first = await _socket.ReceiveAsync(
            _receiveBuffer.AsMemory(),
            cancellationToken);

        if (first.MessageType == WebSocketMessageType.Close)
            throw new IOException("FiveM closed the DevTools connection.");

        if (first.EndOfMessage)
        {
            using JsonDocument document = JsonDocument.Parse(_receiveBuffer.AsMemory(0, first.Count));
            return document.RootElement.Clone();
        }

        using var stream = new MemoryStream(Math.Max(64 * 1024, first.Count * 2));
        stream.Write(_receiveBuffer, 0, first.Count);

        while (true)
        {
            ValueWebSocketReceiveResult next = await _socket.ReceiveAsync(
                _receiveBuffer.AsMemory(),
                cancellationToken);

            if (next.MessageType == WebSocketMessageType.Close)
                throw new IOException("FiveM closed the DevTools connection.");

            stream.Write(_receiveBuffer, 0, next.Count);
            if (next.EndOfMessage) break;
        }

        using JsonDocument document2 = JsonDocument.Parse(
            stream.GetBuffer().AsMemory(0, checked((int)stream.Length)));
        return document2.RootElement.Clone();
    }

    private static string? FindClientFrameId(JsonElement result)
        => result.TryGetProperty("frameTree", out JsonElement tree) ? FindClientFrameIdIn(tree) : null;

    private static string? FindClientFrameIdIn(JsonElement tree)
    {
        if (tree.TryGetProperty("frame", out JsonElement frame) &&
            frame.TryGetProperty("url", out JsonElement url) &&
            url.GetString()?.StartsWith(ClientFramePrefix, StringComparison.OrdinalIgnoreCase) == true &&
            frame.TryGetProperty("id", out JsonElement id))
            return id.GetString();

        if (!tree.TryGetProperty("childFrames", out JsonElement children) ||
            children.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement child in children.EnumerateArray())
        {
            string? match = FindClientFrameIdIn(child);
            if (match is not null) return match;
        }

        return null;
    }

    private static bool IsLoopback(Uri uri)
    {
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _http.Dispose();
    }
}
