using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace BambuStreamer;

public static class BambuCameraStream
{
    private const string Username = "bblp";

    private const int Port = 6000;
    private const int MaxConnectAttempts = 12;
    private const int HeaderSize = 16;
    private const int AuthPacketSize = 80;

    // Auth packet layout
    private const uint AuthMagic1 = 0x40u;
    private const uint AuthMagic2 = 0x3000u;
    private const int UsernameOffset = 16;
    private const int UsernameLength = 32;
    private const int AccessCodeOffset = 48;
    private const int AccessCodeLength = 32;

    // Socket timeouts (ms)
    private const int ReceiveTimeoutMs = 10_000;

    // Reconnect backoff
    private const int InitialBackoffMs = 1_000;
    private const int MaxBackoffMs = 30_000;
    private const int InnerErrorDelayMs = 500;

    // Keep this conservative to avoid renting huge buffers from corrupt/malicious payload sizes.
    private const int MaxPayloadSize = 16 * 1024 * 1024;

    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF];
    private static ReadOnlySpan<byte> JpegEnd => [0xFF, 0xD9];

    private static readonly string AccessCode =
        Environment.GetEnvironmentVariable("PRINTER_ACCESS_CODE")
        ?? throw new InvalidOperationException("PRINTER_ACCESS_CODE environment variable not set");

    private static readonly string Hostname =
        Environment.GetEnvironmentVariable("PRINTER_ADDRESS")
        ?? throw new InvalidOperationException("PRINTER_ADDRESS environment variable not set");
    
    private static readonly string ImagesDir = Path.Combine(AppContext.BaseDirectory, "Images");

    public static async Task RunAsync(bool writeImages)
    {
        if (writeImages)
        {
            Directory.CreateDirectory(ImagesDir);
        }

        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var authData = BuildAuthData();
        await using var stdout = Console.OpenStandardOutput();

        var connectAttempts = 0;
        var backoffMs = InitialBackoffMs;

        while (connectAttempts < MaxConnectAttempts && !cts.IsCancellationRequested)
        {
            connectAttempts++;

            try
            {
                using var client = new TcpClient();
                client.ReceiveTimeout = ReceiveTimeoutMs;

                await client.ConnectAsync(Hostname, Port, cts.Token);

                // Printer uses a self-signed certificate on the LAN; skip validation intentionally.
                await using var sslStream = new SslStream(
                    client.GetStream(), false, (_, _, _, _) => true);

                await sslStream.AuthenticateAsClientAsync(Hostname);

                await sslStream.WriteAsync(authData, cts.Token);
                await sslStream.FlushAsync(cts.Token);

                await Console.Error.WriteLineAsync("Connected and authenticated successfully.");

                // Reset attempt counter and backoff on a fully successful connection.
                connectAttempts = 0;
                backoffMs = InitialBackoffMs;

                await ReceiveImagesAsync(sslStream, stdout, writeImages, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"Connection error: {ex.Message}");

                try
                {
                    await Task.Delay(backoffMs, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
            }
        }

        if (cts.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync("Shutdown requested.");
        }
        else
        {
            await Console.Error.WriteLineAsync("Max connection attempts reached.");
        }
    }

    private static byte[] BuildAuthData()
    {
        var packet = new byte[AuthPacketSize];
        var span = packet.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], AuthMagic1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], AuthMagic2);
        // Bytes 8..16 are left as zero.

        WriteAsciiFixed(span.Slice(UsernameOffset, UsernameLength), Username);
        WriteAsciiFixed(span.Slice(AccessCodeOffset, AccessCodeLength), AccessCode);

        return packet;
    }

    private static void WriteAsciiFixed(Span<byte> destination, string value)
    {
        if (!Ascii.IsValid(value))
        {
            throw new ArgumentException("Value must be ASCII.", nameof(value));
        }

        var byteCount = Encoding.ASCII.GetByteCount(value);

        if (byteCount > destination.Length)
        {
            throw new InvalidOperationException(
                $"Value is too long. Maximum ASCII byte length is {destination.Length}.");
        }

        Encoding.ASCII.GetBytes(value.AsSpan(), destination);
        // Caller-provided destination is assumed zero-initialized (new byte[]).
    }

    private static async Task ReceiveImagesAsync(SslStream sslStream, Stream stdout, 
        bool writeImages, CancellationToken ct)
    {
        var pool = ArrayPool<byte>.Shared;
        var headerBuffer = pool.Rent(HeaderSize);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var header = headerBuffer.AsMemory(0, HeaderSize);
                    await sslStream.ReadExactlyAsync(header, ct);

                    var payloadSize = BinaryPrimitives.ReadInt32LittleEndian(header.Span[..4]);

                    if ((uint)payloadSize > MaxPayloadSize)
                    {
                        await Console.Error.WriteLineAsync($"Invalid payload size: {payloadSize}");
                        return;
                    }

                    var buffer = pool.Rent(payloadSize);

                    try
                    {
                        await sslStream.ReadExactlyAsync(buffer.AsMemory(0, payloadSize), ct);

                        var image = buffer.AsMemory(0, payloadSize);

                        if (IsJpeg(image.Span))
                        {
                            await stdout.WriteAsync(image, ct);
                            await stdout.FlushAsync(ct);

                            if (writeImages)
                            {
                                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                                var imagePath = Path.Combine(ImagesDir, $"image_{timestamp}.jpg");

                                await File.WriteAllBytesAsync(imagePath, image, ct);
                            }
                        }
                        else
                        {
                            await Console.Error.WriteLineAsync("Received non-JPEG payload; skipping.");
                        }
                    }
                    finally
                    {
                        pool.Return(buffer);
                    }
                }
                catch (EndOfStreamException)
                {
                    await Console.Error.WriteLineAsync("Connection closed.");
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or SocketException)
                {
                    await Console.Error.WriteLineAsync($"Error reading from stream: {ex.Message}");

                    try
                    {
                        await Task.Delay(InnerErrorDelayMs, ct);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    // Stream state is unknown after an I/O error; bail out and let the outer loop reconnect.
                    return;
                }
            }
        }
        finally
        {
            pool.Return(headerBuffer);
        }
    }

    private static bool IsJpeg(ReadOnlySpan<byte> data) =>
        data.StartsWith(JpegStart) && data.EndsWith(JpegEnd);
}