using BambuStreamer;

if (HasArg(args, "--help") || HasArg(args, "-h"))
{
    WriteHelp();
    return;
}

var accessCode = GetArgOrEnvVariable(args, "--code", "PRINTER_ACCESS_CODE");
var printerIpAddress = GetArgOrEnvVariable(args, "--ip", "PRINTER_ADDRESS");
var writeImages = HasArg(args, "--write-images");

if (string.IsNullOrEmpty(accessCode) || string.IsNullOrEmpty(printerIpAddress))
{
    Console.WriteLine("Access code and printer IP are required.");
    Console.WriteLine();
    WriteHelp();
    return;
}

await BambuCameraStream.RunAsync(accessCode, printerIpAddress, writeImages);
return;

static string? GetArgOrEnvVariable(string[] args, string key, string envVariableName)
{
    return GetArgValue(args, key) ?? Environment.GetEnvironmentVariable(key);
}

static string? GetArgValue(string[] a, string key)
{
    var idx = Array.IndexOf(a, key);
    return idx >= 0 && idx + 1 < a.Length ? a[idx + 1] : null;
}

static bool HasArg(string[] a, string key) => Array.IndexOf(a, key) >= 0;

static void WriteHelp()
{
    Console.WriteLine("Usage: BambuStreamer [--code <access_code>] [--ip <printer_ip>] [--write-images]");
}