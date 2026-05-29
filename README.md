# BambuStreamer

BambuStreamer provides a Dockerized camera feed bridge for a **Bambu Lab P1S** printer.

It uses a small native AOT-compiled .NET application to connect to the printer camera stream and output JPEG frames. The container combines this with [go2rtc](https://github.com/AlexxIT/go2rtc), making the printer image feed available through go2rtc.

## Notice

Based on https://github.com/bambulab/BambuStudio/issues/1536#issuecomment-1811916472 \
Took some inspiration from [bambu-go2rtc](https://github.com/synman/bambu-go2rtc/tree/main)

## What It Does

BambuStreamer connects directly to the Bambu Lab printer camera endpoint using:

- The printer IP address
- The printer access code

Inside the Docker image, the native AOT-compiled `BambuStreamer` executable is used as an `exec` stream source for go2rtc.
This allows go2rtc to expose the Bambu Lab P1S printer image feed through its supported APIs and streaming integrations.

## Features

- Streams the Bambu Lab P1S camera feed
- Runs inside a Docker container
- Uses go2rtc as the streaming frontend
- Uses a native AOT-compiled .NET application for the printer connection
- Supports configuration through environment variables
- Includes automatic reconnect behavior
- Can optionally write received JPEG images to disk when running the console application directly

## Requirements

- Docker
- A Bambu Lab P1S printer on the same network
- The printer IP address
- The printer access code

## Docker Usage

### Build the Docker Image

```bash
docker build -t bambu-streamer .
```

### Run the Container

```bash
docker run --rm \
  -e PRINTER_ADDRESS=192.168.1.50 \
  -e PRINTER_ACCESS_CODE=12345678 \
  -p 1984:1984 \
  bambu-streamer
```

Replace the values with your printer details:

- `PRINTER_ADDRESS`: IP address of your Bambu Lab P1S
- `PRINTER_ACCESS_CODE`: Access code for your printer

## go2rtc

The Docker image includes go2rtc and generates a `go2rtc.yaml` configuration inside the container.

The configured stream is:

```yaml
streams:
  p1s: "exec: ./BambuStreamer --ip ${PRINTER_ADDRESS} --code ${PRINTER_ACCESS_CODE}"
```

By default, go2rtc starts when the container starts.

After running the container, the go2rtc web interface is typically available at:

```text
http://localhost:1984
```

The printer camera stream is exposed as:

```text
p1s
```

## Architecture Notes

The Dockerfile currently downloads the **amd64** Linux build of go2rtc:

```text
go2rtc_linux_amd64
```

If you want to run this image on another architecture, such as ARM64, you must change the go2rtc download URL in the Dockerfile.

Specifically, update the architecture part on **line 22** of the Dockerfile to match your target platform.

For example, for ARM64 you would need to use the ARM64 go2rtc binary instead of the amd64 one.

Check the available go2rtc release assets here:

```text
https://github.com/AlexxIT/go2rtc/releases
```

## Environment Variables

| Variable | Required | Description |
| --- | --- | --- |
| `PRINTER_ADDRESS` | Yes | IP address of the Bambu Lab P1S printer |
| `PRINTER_ACCESS_CODE` | Yes | Access code used to authenticate with the printer |

## Running the Console Application Directly

The native application can also be run directly outside of Docker.

### Usage

```bash
BambuStreamer [--code <access_code>] [--ip <printer_ip>] [--write-images]
```

### Options

| Option | Description |
| --- | --- |
| `--code <access_code>` | Printer access code |
| `--ip <printer_ip>` | Printer IP address |
| `--write-images` | Writes received JPEG images to disk instead of streaming them to standard output |
| `--help`, `-h` | Shows usage information |

### Example

```bash
dotnet run -- --code 12345678 --ip 192.168.1.50
```

### Using Environment Variables

Instead of passing arguments directly, you can also use environment variables:

```bash
export PRINTER_ADDRESS=192.168.1.50
export PRINTER_ACCESS_CODE=12345678

dotnet run
```

On Windows PowerShell:

```powershell
$env:PRINTER_ADDRESS="192.168.1.50"
$env:PRINTER_ACCESS_CODE="12345678"

dotnet run
```

## Writing Images to Disk

When running the console application directly, you can write received JPEG frames to disk:

```bash
dotnet run -- --code 12345678 --ip 192.168.1.50 --write-images
```

This is mainly useful for debugging or testing the printer camera connection without go2rtc.

## Security Notes

Treat your printer access code like a password.

Avoid committing real printer IP addresses, access codes, or private network details to source control. Prefer passing them through environment variables when running the container.