using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;

namespace Moz_Avalonia.Services;

public static class PortAllocator
{
    private static readonly HashSet<int> Reserved = [];
    private static readonly object Gate = new();

    public static int FindAvailable(int preferred)
    {
        lock (Gate)
        {
            for (var port = preferred; port <= 65535; port++)
            {
                if (Reserved.Contains(port))
                    continue;
                try
                {
                    using var listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    Reserved.Add(port);
                    return port;
                }
                catch (SocketException)
                {
                }
            }

            using var fallback = new TcpListener(IPAddress.Loopback, 0);
            fallback.Start();
            var selected = ((IPEndPoint)fallback.LocalEndpoint).Port;
            Reserved.Add(selected);
            return selected;
        }
    }

    public static void Release(int port)
    {
        lock (Gate) Reserved.Remove(port);
    }
}
