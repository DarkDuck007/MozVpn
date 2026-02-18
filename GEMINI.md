# MozVPN Project

## Project Overview

This is the repository for MozVPN, a peer-to-peer VPN solution designed to circumvent internet censorship. The project is written in C# and consists of multiple components, including a central ASP.NET signaling server, a shared utility library (`MozUtil`), and several client applications for different platforms (WPF, MAUI, and console).

The core of MozVPN's architecture revolves around UDP hole punching for establishing direct peer-to-peer connections, minimizing reliance on a central server for data transfer.

### Key Components

*   **MozASPServer:** An ASP.NET Core web application that acts as a signaling server. Clients connect to this server to discover each other and initiate the connection process.
*   **MozUtil:** A shared library containing the core logic for networking, NAT traversal (using STUN), connection management, and data transfer.
*   **MozVpnWPF:** A Windows Presentation Foundation (WPF) client application providing a graphical user interface for connecting to the VPN.
*   **MozVpnMAUI:** A .NET Multi-platform App UI (MAUI) client for cross-platform support (Android, iOS, macOS, and Windows).
*   **MozClientConsole / MozVPN_CLI:** Console-based client applications for scripting and headless operation.

### Connection Flow

1.  **STUN Check:** The client application uses a STUN server to determine its public IP address, port, and NAT type.
2.  **Signaling:** The client sends its STUN information to the `MozASPServer` via an HTTP long-polling request.
3.  **Peer Matching:** The signaling server matches two clients that want to connect and sends each client the other's connection information.
4.  **UDP Hole Punching:** The clients use the received information to perform UDP hole punching, creating a direct communication path through their NATs.
5.  **Direct Connection:** Once the hole punching is successful, the clients establish a direct peer-to-peer connection using the LiteNetLib library for reliable UDP communication. Local TCP traffic is then tunneled over this connection.

## Building and Running

The project is a standard Visual Studio solution (`MozVpn.sln`). To build and run the project:

1.  Open `MozVpn.sln` in Visual Studio.
2.  Build the solution to restore NuGet packages and compile all projects.
3.  **Running the Server:** Set `MozASPServer` as the startup project and run it. This will start the signaling server.
4.  **Running a Client:** Set one of the client projects (e.g., `MozVpnWPF` or `MozClientConsole`) as the startup project and run it.

### TODO: Configuration

The specific configuration for the signaling server URL and STUN server addresses needs to be documented. It appears to be hardcoded in the client applications for now.

## Development Conventions

*   **Asynchronous Programming:** The codebase makes extensive use of `async`/`await` for non-blocking network operations.
*   **Networking:** The project uses low-level networking with `UdpClient`, `TcpClient`, and `HttpWebRequest`. For the primary data transport, it relies on the `LiteNetLib` library.
*   **Dependency Management:** NuGet is used for package management.
*   **Logging:** A simple, custom `Logger` class is used for logging throughout the `MozUtil` library.

