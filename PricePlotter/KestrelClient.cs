using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace mine;

public enum ConnectionStatus { Connected, Disconnected }

public readonly record struct RelayMessage(string Channel, ReadOnlyMemory<byte> Payload, DateTimeOffset Timestamp)
{
    public string GetString() => Encoding.UTF8.GetString(Payload.Span);
    public T? Deserialize<T>(JsonSerializerOptions? options = null) => JsonSerializer.Deserialize<T>(Payload.Span, options);
    public T? Deserialize<T>(JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(Payload.Span, typeInfo);
}

public sealed class KestrelClient : IAsyncDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int TimestampSize = 8;
    private const int HeaderSize = NonceSize + TagSize + TimestampSize; // 36 bytes: [12 Nonce][16 Tag][8 UTC Unix Ms]
    private const int MaxMessageSize = 1024 * 1024; // 1 MB max payload limit
    private const long MaxClockSkewMs = 60_000; // +/- 60 seconds anti-replay window

    // Replace with your own 32-byte (256-bit) key generated via Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    private static readonly byte[] _defaultSharedKey = Convert.FromBase64String("9J7kL2mP4qR8sT1vW3xY6zA0bC5dE7fG9hI2jK4lM6n=");

    private readonly byte[] _sharedKey;
    private readonly string _serverIpAddress;
    private readonly int _serverPort;
    private readonly ConcurrentDictionary<string, ChannelConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<RelayMessage> _incomingChannel = Channel.CreateUnbounded<RelayMessage>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly List<Task> _backgroundLoops = [];
    private CancellationTokenSource? _cts;
    private int _state = 0; // 0 = Idle, 1 = Running, 2 = Disposed

    public event Action<string, ConnectionStatus>? OnConnectionStateChanged;

    public KestrelClient(string serverIpAddress = "192.168.1.50", int serverPort = 8080, byte[]? sharedKey = null)
    {
        _serverIpAddress = serverIpAddress;
        _serverPort = serverPort;
        _sharedKey = sharedKey ?? _defaultSharedKey;
        if (_sharedKey.Length != 32) throw new ArgumentException("Shared key must be exactly 32 bytes (256 bits).", nameof(sharedKey));
    }

    public void Start(params ReadOnlySpan<string> channels)
    {
        if (channels.IsEmpty) throw new ArgumentException("At least one channel must be specified.", nameof(channels));
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0) return;

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        foreach (string rawChannel in channels)
        {
            if (string.IsNullOrWhiteSpace(rawChannel)) continue;
            string normalized = rawChannel.Trim().Trim('/').ToLowerInvariant();

            ChannelConnection conn = new(normalized, _sharedKey);
            if (_connections.TryAdd(normalized, conn))
            {
                _backgroundLoops.Add(Task.Run(() => ConnectAndReceiveLoopAsync(conn, token), CancellationToken.None));
            }
            else
            {
                conn.Dispose();
            }
        }
    }

    public IAsyncEnumerable<RelayMessage> ReadAllAsync(CancellationToken cancellationToken = default) => _incomingChannel.Reader.ReadAllAsync(cancellationToken);

    public ValueTask SendTextAsync(string channel, ReadOnlySpan<char> message, CancellationToken cancellationToken = default)
    {
        int maxBytes = Encoding.UTF8.GetMaxByteCount(message.Length);
        byte[] rented = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int actualBytes = Encoding.UTF8.GetBytes(message, rented);
            return EncryptAndSendCoreAsync(channel, rented, actualBytes, cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            throw;
        }
    }

    public ValueTask SendJsonAsync<T>(string channel, T payload, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte> writer = new(256);
        using (Utf8JsonWriter jsonWriter = new(writer))
        {
            JsonSerializer.Serialize(jsonWriter, payload, options);
        }
        return EncryptAndSendMemoryAsync(channel, writer.WrittenMemory, cancellationToken);
    }

    public ValueTask SendJsonAsync<T>(string channel, T payload, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        ArrayBufferWriter<byte> writer = new(256);
        using (Utf8JsonWriter jsonWriter = new(writer))
        {
            JsonSerializer.Serialize(jsonWriter, payload, typeInfo);
        }
        return EncryptAndSendMemoryAsync(channel, writer.WrittenMemory, cancellationToken);
    }

    private ValueTask EncryptAndSendMemoryAsync(string channel, ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(plaintext.Length);
        try
        {
            plaintext.Span.CopyTo(rented);
            return EncryptAndSendCoreAsync(channel, rented, plaintext.Length, cancellationToken);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            throw;
        }
    }

    private async ValueTask EncryptAndSendCoreAsync(string channel, byte[] rentedPlaintext, int plaintextLength, CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(channel)) throw new ArgumentException("Channel cannot be null or empty.", nameof(channel));
            string normalized = channel.Trim().Trim('/').ToLowerInvariant();

            if (!_connections.TryGetValue(normalized, out ChannelConnection? conn))
            {
                throw new InvalidOperationException($"Channel '{normalized}' was not registered during Start().");
            }

            ClientWebSocket? socket = conn.Socket;
            if (socket == null || socket.State != WebSocketState.Open) return;

            int packetLength = HeaderSize + plaintextLength;
            byte[] packetBuffer = ArrayPool<byte>.Shared.Rent(packetLength);

            try
            {
                using CancellationTokenSource sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendCts.CancelAfter(TimeSpan.FromSeconds(5));

                await conn.SendLock.WaitAsync(sendCts.Token).ConfigureAwait(false);
                try
                {
                    if (socket.State != WebSocketState.Open) return;

                    BuildEncryptedPacket(conn, rentedPlaintext.AsSpan(0, plaintextLength), packetBuffer.AsSpan(0, packetLength));
                    await socket.SendAsync(packetBuffer.AsMemory(0, packetLength), WebSocketMessageType.Binary, true, sendCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    conn.SendLock.Release();
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(packetBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedPlaintext, clearArray: true);
        }
    }

    private static void BuildEncryptedPacket(ChannelConnection conn, ReadOnlySpan<byte> plaintext, Span<byte> packet)
    {
        Span<byte> nonce = packet.Slice(0, NonceSize);
        Span<byte> tag = packet.Slice(NonceSize, TagSize);
        Span<byte> timestampSpan = packet.Slice(NonceSize + TagSize, TimestampSize);
        Span<byte> ciphertext = packet.Slice(HeaderSize, plaintext.Length);

        RandomNumberGenerator.Fill(nonce);
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        BinaryPrimitives.WriteInt64BigEndian(timestampSpan, nowUnixMs);

        Span<byte> aad = stackalloc byte[conn.ChannelBytes.Length + TimestampSize];
        conn.ChannelBytes.CopyTo(aad);
        timestampSpan.CopyTo(aad.Slice(conn.ChannelBytes.Length));

        // Safe single-threaded invocation because caller holds conn.SendLock
        conn.TxAes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
    }

    private async Task ConnectAndReceiveLoopAsync(ChannelConnection conn, CancellationToken token)
    {
        byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(8192);
        Uri serverUri = new($"ws://{_serverIpAddress}:{_serverPort}/{conn.Channel}/");
        int attempt = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                ClientWebSocket socket = new();
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                conn.Socket = socket;

                try
                {
                    using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(3));

                    await socket.ConnectAsync(serverUri, connectCts.Token).ConfigureAwait(false);
                    attempt = 0; // Reset backoff on successful connection
                    UpdateConnectionState(conn, ConnectionStatus.Connected);

                    while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                    {
                        ValueWebSocketReceiveResult result = await socket.ReceiveAsync(receiveBuffer.AsMemory(), token).ConfigureAwait(false);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await SafeCloseOutputAsync(conn, socket).ConfigureAwait(false);
                            break;
                        }

                        if (result.EndOfMessage)
                        {
                            ProcessIncomingFrame(conn, receiveBuffer.AsSpan(0, result.Count));
                        }
                        else
                        {
                            using MemoryStream ms = new();
                            ms.Write(receiveBuffer.AsSpan(0, result.Count));

                            bool closedDuringFragment = false;
                            do
                            {
                                result = await socket.ReceiveAsync(receiveBuffer.AsMemory(), token).ConfigureAwait(false);
                                if (result.MessageType == WebSocketMessageType.Close)
                                {
                                    await SafeCloseOutputAsync(conn, socket).ConfigureAwait(false);
                                    closedDuringFragment = true;
                                    break;
                                }

                                if (ms.Length + result.Count > MaxMessageSize)
                                {
                                    throw new WebSocketException("Incoming message exceeded 1 MB maximum size.");
                                }

                                ms.Write(receiveBuffer.AsSpan(0, result.Count));
                            } while (!result.EndOfMessage);

                            if (closedDuringFragment) break;

                            ProcessIncomingFrame(conn, ms.GetBuffer().AsSpan(0, (int)ms.Length));
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[KestrelClient:{conn.Channel}] Socket fault: {ex.Message}");
                }
                finally
                {
                    conn.Socket = null;
                    socket.Dispose();
                    UpdateConnectionState(conn, ConnectionStatus.Disconnected);
                }

                if (!token.IsCancellationRequested)
                {
                    attempt++;
                    double baseDelayMs = Math.Min(15_000, 500 * Math.Pow(2, Math.Min(attempt, 6)));
                    int jitteredDelayMs = (int)(baseDelayMs * (0.5 + Random.Shared.NextDouble() * 0.5));

                    try
                    {
                        await Task.Delay(jitteredDelayMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
        }
    }

    private void ProcessIncomingFrame(ChannelConnection conn, ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderSize) return;

        ReadOnlySpan<byte> timestampSpan = packet.Slice(NonceSize + TagSize, TimestampSize);
        long packetUnixMs = BinaryPrimitives.ReadInt64BigEndian(timestampSpan);
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Fast pre-decryption anti-replay check (still cryptographically verified below via AAD)
        if (Math.Abs(nowUnixMs - packetUnixMs) > MaxClockSkewMs)
        {
            System.Diagnostics.Debug.WriteLine($"[KestrelClient:{conn.Channel}] Dropped packet outside 60s anti-replay window.");
            return;
        }

        int ciphertextLength = packet.Length - HeaderSize;
        byte[] plaintext = GC.AllocateUninitializedArray<byte>(ciphertextLength);

        try
        {
            ReadOnlySpan<byte> nonce = packet.Slice(0, NonceSize);
            ReadOnlySpan<byte> tag = packet.Slice(NonceSize, TagSize);
            ReadOnlySpan<byte> ciphertext = packet.Slice(HeaderSize, ciphertextLength);

            Span<byte> aad = stackalloc byte[conn.ChannelBytes.Length + TimestampSize];
            conn.ChannelBytes.CopyTo(aad);
            timestampSpan.CopyTo(aad.Slice(conn.ChannelBytes.Length));

            // Safe single-threaded invocation because each channel runs exactly one receive loop
            conn.RxAes.Decrypt(nonce, ciphertext, tag, plaintext, aad);

            RelayMessage msg = new(conn.Channel, plaintext, DateTimeOffset.FromUnixTimeMilliseconds(packetUnixMs));
            _incomingChannel.Writer.TryWrite(msg);
        }
        catch (AuthenticationTagMismatchException)
        {
            System.Diagnostics.Debug.WriteLine($"[KestrelClient:{conn.Channel}] Security Warning: Rejected packet due to invalid AES-GCM tag or AAD mismatch.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KestrelClient:{conn.Channel}] Decryption error: {ex.Message}");
        }
    }

    private void UpdateConnectionState(ChannelConnection conn, ConnectionStatus newStatus)
    {
        if (conn.LastStatus != newStatus)
        {
            conn.LastStatus = newStatus;
            try
            {
                OnConnectionStateChanged?.Invoke(conn.Channel, newStatus);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[KestrelClient:{conn.Channel}] State callback error: {ex.Message}");
            }
        }
    }

    private static async Task SafeCloseOutputAsync(ChannelConnection conn, ClientWebSocket socket)
    {
        using CancellationTokenSource closeCts = new(TimeSpan.FromSeconds(5));
        await conn.SendLock.WaitAsync(closeCts.Token).ConfigureAwait(false);
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            conn.SendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _state, 2) == 2) return;

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_backgroundLoops.Count > 0)
        {
            await Task.WhenAll(_backgroundLoops).ConfigureAwait(false);
        }

        _incomingChannel.Writer.TryComplete();

        foreach (ChannelConnection conn in _connections.Values)
        {
            conn.Dispose();
        }
        _connections.Clear();

        _cts?.Dispose();
        _cts = null;
    }

    private sealed class ChannelConnection(string channel, byte[] key) : IDisposable
    {
        public string Channel { get; } = channel;
        public byte[] ChannelBytes { get; } = Encoding.UTF8.GetBytes(channel);
        public AesGcm RxAes { get; } = new AesGcm(key, TagSize);
        public AesGcm TxAes { get; } = new AesGcm(key, TagSize);
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public ClientWebSocket? Socket { get; set; }
        public ConnectionStatus LastStatus { get; set; } = ConnectionStatus.Disconnected;

        public void Dispose()
        {
            RxAes.Dispose();
            TxAes.Dispose();
            SendLock.Dispose();
        }
    }
}