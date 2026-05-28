using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace BambuStreamer;

public class BambuCameraStream
{
    private const string Username = "bblp";

    private static string AccessCode => Environment.GetEnvironmentVariable("PRINTER_ACCESS_CODE")
                                        ?? throw new Exception("PRINTER_ACCESS_CODE environment variable not set");

    private static string Hostname => Environment.GetEnvironmentVariable("PRINTER_ADDRESS")
                                      ?? throw new Exception("PRINTER_ADDRESS environment variable not set");

    private const int Port = 6000;
    private const int MaxConnectAttempts = 12;
    private const int ReadChunkSize = 4096;
    private const int AuthPacketSize = 80;
    private const int HeaderSize = 16;

    // Keep this conservative to avoid renting huge buffers from corrupt/malicious payload sizes.
    private const int MaxPayloadSize = 16 * 1024 * 1024;

    private static ReadOnlySpan<byte> JpegStart => [0xFF, 0xD8, 0xFF, 0xE0];
    private static ReadOnlySpan<byte> JpegEnd => [0xFF, 0xD9];

    private static readonly string ImagesDir = Path.Combine(AppContext.BaseDirectory, "Images");

    public static void Run()
    {
        var connectAttempts = 0;
        var authData = BuildAuthData();

        Directory.CreateDirectory(ImagesDir);

        while (connectAttempts < MaxConnectAttempts)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(Hostname, Port);

                connectAttempts++;

                using var sslStream = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                sslStream.AuthenticateAsClient(Hostname);

                sslStream.Write(authData);
                sslStream.Flush();

                Console.Error.WriteLine("Connected and authenticated successfully.");

                ReceiveImages(sslStream);

                connectAttempts = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Connection error: {ex.Message}");
                Thread.Sleep(2000);
            }
        }

        Console.Error.WriteLine("Max connection attempts reached.");
    }

    private static byte[] BuildAuthData()
    {
        var packet = new byte[AuthPacketSize];
        var span = packet.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[..4], 0x40u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..8], 0x3000u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..12], 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..16], 0u);

        WriteAsciiFixed(span.Slice(16, 32), Username);
        WriteAsciiFixed(span.Slice(48, 32), AccessCode);

        return packet;
    }

    private static void WriteAsciiFixed(Span<byte> destination, string value)
    {
        destination.Clear();

        var byteCount = Encoding.ASCII.GetByteCount(value);

        if (byteCount > destination.Length)
        {
            throw new InvalidOperationException($"Value is too long. Maximum ASCII byte length is {destination.Length}.");
        }

        Encoding.ASCII.GetBytes(value.AsSpan(), destination);
    }

    private static void ReceiveImages(SslStream sslStream)
    {
        var pool = ArrayPool<byte>.Shared;
        var readBuffer = pool.Rent(ReadChunkSize);

        byte[]? imageBuffer = null;
        var imageOffset = 0;
        var payloadSize = 0;

        using var stdout = Console.OpenStandardOutput();

        try
        {
            while (true)
            {
                try
                {
                    var bytesRead = sslStream.Read(readBuffer.AsSpan(0, ReadChunkSize));

                    if (bytesRead == 0)
                    {
                        Console.Error.WriteLine("Connection closed.");
                        break;
                    }

                    var chunk = readBuffer.AsSpan(0, bytesRead);

                    if (imageBuffer is null)
                    {
                        if (bytesRead != HeaderSize)
                        {
                            continue;
                        }

                        payloadSize = BinaryPrimitives.ReadInt32LittleEndian(chunk[..4]);

                        if ((uint)payloadSize > MaxPayloadSize)
                        {
                            Console.Error.WriteLine($"Invalid payload size: {payloadSize}");
                            break;
                        }

                        imageBuffer = pool.Rent(payloadSize);
                        imageOffset = 0;
                        continue;
                    }

                    var remaining = payloadSize - imageOffset;
                    var toCopy = Math.Min(chunk.Length, remaining);

                    chunk[..toCopy].CopyTo(imageBuffer.AsSpan(imageOffset));
                    imageOffset += toCopy;

                    if (imageOffset != payloadSize)
                    {
                        continue;
                    }

                    var image = imageBuffer.AsSpan(0, payloadSize);

                    if (IsJpeg(image))
                    {
                        stdout.Write(image);
                        stdout.Flush();

                        // var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                        // var imagePath = Path.Combine(ImagesDir, $"image_{timestamp}.jpg");
                        //
                        // File.WriteAllBytes(imagePath, image);
                    }

                    pool.Return(imageBuffer);
                    imageBuffer = null;
                    imageOffset = 0;
                    payloadSize = 0;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    Console.Error.WriteLine($"Error reading from stream: {ex.Message}");
                    Thread.Sleep(500);
                }
            }
        }
        finally
        {
            pool.Return(readBuffer);

            if (imageBuffer is not null)
            {
                pool.Return(imageBuffer);
            }
        }
    }

    private static bool IsJpeg(ReadOnlySpan<byte> data)
    {
        return data.StartsWith(JpegStart) && data.EndsWith(JpegEnd);
    }
}