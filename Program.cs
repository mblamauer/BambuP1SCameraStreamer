using BambuStreamer;

var writeImages = args.Contains("--write-images");

await BambuCameraStream.RunAsync(writeImages);