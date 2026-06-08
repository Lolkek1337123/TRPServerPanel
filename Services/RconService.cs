using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace TRPServerPanel.Services
{
    public class RconMessage
    {
        public int Identifier { get; set; }
        public string Message { get; set; } = "";
        public string Name { get; set; } = "WebRcon";
        public string Stacktrace { get; set; } = "";
        public string Type { get; set; } = "Generic";
    }

    public class RconService : IDisposable
    {
        private ClientWebSocket? _webSocket;
        private CancellationTokenSource? _cts;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingRequests = new();
        private int _identifierCounter = 1000;
        private readonly SemaphoreSlim _sendSemaphore = new(1, 1);

        public bool IsConnected => _webSocket?.State == WebSocketState.Open;
        public event Action<string>? OnMessageReceived;

        public async Task ConnectAsync(string ip, int port, string password)
        {
            if (IsConnected) await DisconnectAsync();

            _webSocket = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            string url = $"ws://{(ip == "0.0.0.0" ? "127.0.0.1" : ip)}:{port}/{password}";
            
            try
            {
                await Task.Run(async () =>
                {
                    await _webSocket.ConnectAsync(new Uri(url), _cts.Token);
                });
                _ = ReceiveLoop();
                _ = KeepAliveLoop();
                AppLogService.Log($"Connected to RCON at {ip}:{port}", AppLogLevel.INFO, "RCON");
            }
            catch (Exception ex)
            {
                AppLogService.Log($"Connection failed to {ip}:{port}: {ex.Message}", AppLogLevel.ERROR, "RCON");
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            _cts?.Cancel();
            if (_webSocket != null)
            {
                try
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        using (var closeTimeout = new CancellationTokenSource(500))
                        {
                            await Task.Run(async () =>
                            {
                                await _webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeTimeout.Token);
                            });
                        }
                    }
                }
                catch { }
                finally
                {
                    try { _webSocket.Dispose(); } catch { }
                    _webSocket = null;
                }
            }

            foreach (var req in _pendingRequests.Values)
            {
                req.TrySetResult("Disconnected");
            }
            _pendingRequests.Clear();
        }

        public async Task<string> SendCommandWithResponseAsync(string command, int timeoutMs = 5000)
        {
            if (!IsConnected) return "";

            int id = Interlocked.Increment(ref _identifierCounter);
            var tcs = new TaskCompletionSource<string>();
            _pendingRequests[id] = tcs;

            try
            {
                await SendCommandAsync(command, id);
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                
                if (completedTask == tcs.Task)
                {
                    return await tcs.Task;
                }
                else
                {
                    return "Timeout";
                }
            }
            finally
            {
                _pendingRequests.TryRemove(id, out _);
            }
        }

        public async Task SendCommandAsync(string command, int identifier = 0)
        {
            if (!IsConnected) return;

            var rconMsg = new RconMessage
            {
                Identifier = identifier,
                Message = command,
                Name = "WebRcon"
            };

            string json = JsonSerializer.Serialize(rconMsg);
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            AppLogService.Log($"Sending Command: {command}", AppLogLevel.DEBUG, "RCON");
            await _sendSemaphore.WaitAsync();
            try
            {
                await Task.Run(async () =>
                {
                    await _webSocket!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts!.Token);
                });
            }
            finally
            {
                _sendSemaphore.Release();
            }
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[65536]; // 64KB buffer for large status responses
            try
            {
                while (IsConnected && _cts != null && !_cts.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _webSocket!.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                        if (result.MessageType == WebSocketMessageType.Close) break;

                        string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        if (string.IsNullOrEmpty(message)) continue;

                        // Support fragmented messages
                        while (!result.EndOfMessage)
                        {
                            result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);
                            message += Encoding.UTF8.GetString(buffer, 0, result.Count);
                        }

                        // v16.6: Log raw RCON message for deep diagnostics
                        AppLogService.Log($"[RCON RECV] {message.Substring(0, Math.Min(message.Length, 500))}{(message.Length > 500 ? "..." : "")}", AppLogLevel.DEBUG, "RCON");

                        try {
                            var parsed = JsonSerializer.Deserialize<RconMessage>(message);
                            if (parsed != null)
                            {
                                if (_pendingRequests.TryGetValue(parsed.Identifier, out var tcs))
                                {
                                    tcs.TrySetResult(parsed.Message);
                                }
                                OnMessageReceived?.Invoke(parsed.Message);
                            }
                        } catch {
                            OnMessageReceived?.Invoke(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[RCON] Receive Error: {ex.Message}");
                        break;
                    }
                }
            }
            finally
            {
                _ = DisconnectAsync();
            }
        }

        private async Task KeepAliveLoop()
        {
            while (IsConnected && _cts != null && !_cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token);
                    if (IsConnected)
                    {
                        await SendCommandAsync("echo keepalive");
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RCON] KeepAlive error: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            DisconnectAsync().Wait();
            _sendSemaphore.Dispose();
        }
    }
}
