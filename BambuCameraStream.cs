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

    private static readonly byte[] JpegStart = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] JpegEnd = [0xFF, 0xD9];

    public static void Run()
    {
        var connectAttempts = 0;
        var authData = BuildAuthData();

        while (connectAttempts < MaxConnectAttempts)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect(Hostname, Port);

                connectAttempts++;

                using var sslStream = new SslStream(client.GetStream(), false, (_, _, _, _) => true, null);
                sslStream.AuthenticateAsClient(Hostname);

                sslStream.Write(authData);
                sslStream.Flush();

                Console.Error.WriteLine("Connected and authenticated successfully.");

                ReceiveImages(sslStream);
                connectAttempts = 0; // Reset on successful connection
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
        using var ms = new MemoryStream();

        // Header
        ms.Write(BitConverter.GetBytes(0x40u), 0, 4); // 0x40
        ms.Write(BitConverter.GetBytes(0x3000u), 0, 4); // 0x3000
        ms.Write(BitConverter.GetBytes(0u), 0, 4); // 0
        ms.Write(BitConverter.GetBytes(0u), 0, 4); // 0

        // Username (32 bytes)
        var userBytes = Encoding.ASCII.GetBytes(Username);
        ms.Write(userBytes, 0, userBytes.Length);
        ms.Write(new byte[32 - userBytes.Length], 0, 32 - userBytes.Length);

        // Access Code (32 bytes)
        var codeBytes = Encoding.ASCII.GetBytes(AccessCode);
        ms.Write(codeBytes, 0, codeBytes.Length);
        ms.Write(new byte[32 - codeBytes.Length], 0, 32 - codeBytes.Length);

        return ms.ToArray();
    }

    private static void ReceiveImages(SslStream sslStream)
    {
        var buffer = new byte[ReadChunkSize];
        byte[]? imageBuffer = null;
        var imageOffset = 0;
        var payloadSize = 0;

        while (true)
        {
            try
            {
                var bytesRead = sslStream.Read(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    Console.Error.WriteLine("Connection closed.");
                    break;
                }

                // New header?
                if (imageBuffer == null && bytesRead == 16)
                {
                    payloadSize = BitConverter.ToInt32(buffer, 0);
                    imageBuffer = new byte[payloadSize];
                    imageOffset = 0;
                    continue;
                }

                // Append to current image
                if (imageBuffer != null)
                {
                    var toCopy = Math.Min(bytesRead, payloadSize - imageOffset);
                    Array.Copy(buffer, 0, imageBuffer, imageOffset, toCopy);
                    imageOffset += toCopy;

                    if (imageOffset == payloadSize)
                    {
                        // Full image received
                        if (StartsWith(imageBuffer, JpegStart) && EndsWith(imageBuffer, JpegEnd))
                        {
                            Console.OpenStandardOutput().Write(imageBuffer, 0, imageBuffer.Length);
                            Console.OpenStandardOutput().Flush();
                        }

                        // Reset for next image
                        imageBuffer = null;
                        imageOffset = 0;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is TimeoutException)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool EndsWith(byte[] data, byte[] suffix)
    {
        if (data.Length < suffix.Length)
        {
            return false;
        }

        for (var i = 0; i < suffix.Length; i++)
        {
            if (data[data.Length - suffix.Length + i] != suffix[i])
            {
                return false;
            }
        }

        return true;
    }
}