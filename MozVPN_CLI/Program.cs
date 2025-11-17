using MozUtil;
using MozUtil.NatUtils;
using STUN;
using System.Xml.Linq;

namespace MozVPN_CLI
{
    enum MZProto
    {
        Default = 0,
        SplitDuplex = 1,
        WebSocket = 2
    }
    class MozConfig
    {
        public int MaxChannels { get; set; } = 32;
        public string StunServer { get; set; } = "stun4.l.google.com:19302";
        public string ServerAddress { get; set; } = "http://dirtypx.somee.com/";
        public int Port { get; set; } = 6075;
        public int HPort { get; set; } = 6085;
        public bool SkipStun { get; set; } = false;
        public string StunSpoofLocalEP { get; set; } = "0.0.0.0";
        public string StunSpoofPublicEP { get; set; } = "0.0.0.0";
        public STUNNATType StunSpoofNatType { get; set; } = STUNNATType.PortRestricted;
        public string? Proxy { get; set; } = null;
    }
    internal class Program
    {
        static void Main(string[] args)
        {
            MozConfig Config = new();
            while (true)
            {
                Console.Clear();
                Console.WriteLine("Current configuration: ");
                Console.WriteLine($"1. Server: {Config.ServerAddress}");
                Console.WriteLine($"2. StunServer: {Config.StunServer}");
                Console.WriteLine($"3. MaxChannels: {Config.MaxChannels}");
                Console.WriteLine($"4. Port(socks): {Config.Port}");
                Console.WriteLine($"41. Port(HTTP): {Config.HPort}");
                Console.WriteLine($"5. Skip stun {Config.SkipStun}");
                Console.WriteLine($"6. StunSpoofLocalEP (applies if skipped) {Config.StunSpoofLocalEP}");
                Console.WriteLine($"7. StunSpoofPublicEP (applies if skipped) {Config.StunSpoofPublicEP}");
                Console.WriteLine($"8. StunSpoofNatType (applies if skipped) {Config.StunSpoofNatType}");
                Console.WriteLine($"9. Proxy (applies if skipped) {Config.Proxy}");
                Console.WriteLine($"Enter the index of the config you want to change OR 0 to run.");
                string input = Console.ReadLine();
                bool tst = int.TryParse(input, out int index);
                if (!tst)
                {
                    continue;
                }
                switch (index)
                {
                    case 0:
                        Console.WriteLine("Running with current configuration...");
                        break;
                    case 1:
                        Console.WriteLine("Enter new Server Address (include / at the end of the url.): ");
                        Config.ServerAddress = Console.ReadLine();
                        break;
                    case 2:
                        Console.WriteLine("Enter new StunServer: ");
                        Config.StunServer = Console.ReadLine();
                        break;
                    case 3:
                        Console.WriteLine("Enter new MaxChannels <1-64>: ");
                        string maxChannelsInput = Console.ReadLine();
                        if (int.TryParse(maxChannelsInput, out int maxChannels))
                        {
                            if (maxChannels < 1 || maxChannels > 64)
                            {
                                Console.WriteLine("Select a value between 1-64");
                                break;
                            }
                            Config.MaxChannels = maxChannels;
                        }
                        else
                        {
                            Console.WriteLine("Invalid input for MaxChannels. Please enter a valid number.");
                        }
                        break;
                    case 4:
                        Console.WriteLine("Enter new port <1-65535>");
                        string portInput = Console.ReadLine();
                        if (int.TryParse(portInput, out int port))
                        {
                            if (port < 1 || port > 65535)
                            {
                                Console.WriteLine("Enter a value between 1-65535");
                                break;
                            }
                            Config.Port = port;
                        }
                        break;
                    case 5:
                        string NewValue = Console.ReadLine();
                        if (NewValue.Equals("true", StringComparison.InvariantCultureIgnoreCase))
                        {
                            Config.SkipStun = true;
                        }
                        else
                        {
                            Config.SkipStun = false;
                        }
                        break;
                    case 6:
                        Console.WriteLine("Enter new StunSpoofLocalEP");
                        Config.StunSpoofLocalEP = Console.ReadLine();
                        break;
                    case 7:
                        Console.WriteLine("Enter new StunSpoofPublicEP");
                        Config.StunSpoofPublicEP = Console.ReadLine();
                        break;
                    case 8:
                        Console.WriteLine("Enter new StunSpoofNatType");
                        Console.WriteLine($"{((int)STUNNATType.Unspecified)}, Unspecified");
                        Console.WriteLine($"{((int)STUNNATType.OpenInternet)}, OpenInternet");
                        Console.WriteLine($"{((int)STUNNATType.Restricted)}, Restricted");
                        Console.WriteLine($"{((int)STUNNATType.PortRestricted)}, PortRestricted");
                        Console.WriteLine($"{((int)STUNNATType.Symmetric)}, Symmetric");
                        Console.WriteLine($"{((int)STUNNATType.SymmetricUDPFirewall)}, SymmetricUDPFirewall");
                        Config.StunSpoofNatType = (STUNNATType)int.Parse(Console.ReadLine());
                        break;
                        case 9:
                        Console.WriteLine("proxy format: protocol://ip:port");
                        Config.Proxy = Console.ReadLine();
                        break;
                    case 41:
                        Console.WriteLine("Enter http port");
                        Config.HPort = int.Parse(Console.ReadLine());
                        break;
                    default:
                        Console.WriteLine("Invalid index. Please try again.");
                        Console.ReadLine();
                        break;
                }
                if (index == 0)
                {
                    break;
                }
            }
            Console.WriteLine("Starting...");
            bool useProxy = (Config.Proxy is not null);
            MozManager Manager = new MozManager(Config.ServerAddress, ((byte)Config.MaxChannels), Config.StunServer, Config.Port, Config.HPort, 10000,
                  false, TransportMode.LiteNet, useProxy, Config.Proxy, false, Config.SkipStun, Config.StunSpoofLocalEP, Config.StunSpoofPublicEP, Config.StunSpoofNatType);
            Manager.NewLogArrived += Manager_NewLogArrived;
            Task.Run(async () =>
            {
                bool Result = await Manager.InitiateConnection();

            });
            Console.ReadLine();
        }

        private static void Manager_NewLogArrived(object? sender, string e)
        {
            //Console.WriteLine(e);
        }
    }
}
