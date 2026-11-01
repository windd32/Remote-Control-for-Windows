# Remote Control for Windows

A small WPF prototype for direct TCP remote desktop connections. The controller listens for a connection; the client shows a consent prompt before sharing its screen or accepting mouse and keyboard input. The client connection screen opens by default.

## Features

- Controller mode listens on a configurable TCP port and displays the remote desktop.
- Client mode connects to the controller by IP address and port.
- Screen sharing and input control start only after the client user approves the request.
- Closing the client window ends the session.

## Build

Requires Windows 10/11 and the .NET 8 SDK with Windows Desktop support.

```powershell
dotnet build .\RemoteControl\RemoteControl.csproj -c Release
```

To create a self-contained Windows x64 executable:

```powershell
dotnet publish .\RemoteControl\RemoteControl.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Connect

1. On the controller PC, select **Controller**, choose a TCP port (default `45831`), and click **Start listening**.
2. Allow the application through Windows Firewall and forward that TCP port to the controller PC on your router.
3. On the remote PC, enter the controller's public IP address and port, then click **Connect**.
4. The remote user must approve the consent prompt on that PC before screen sharing and control begin.

The controller needs a reachable public address and open port. A private/CGNAT address on the client side is fine because the client initiates the outbound connection. If the controller is behind CGNAT, direct inbound IPv4 will not work without public IPv6 or another network arrangement.

## Security and limitations

This is an experimental prototype, not a security-reviewed remote access product. The protocol does not encrypt traffic or provide strong peer authentication. Do not expose it to untrusted networks or leave its port open. A production version needs authenticated TLS, secure device pairing, robust error handling, and a security review. Screen capture currently uses the primary monitor at reduced resolution; some protected Windows surfaces may not be captured.

## GitHub description

`Consent-based Windows remote desktop prototype over direct TCP, with screen streaming and mouse/keyboard input.`
