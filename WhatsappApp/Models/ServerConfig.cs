namespace WhatsappApp.Models
{
    public enum ConnectionMode
    {
        ClientMode,
        ServerMode
    }

    public class ServerConfig
    {
        public ConnectionMode Mode { get; set; }
        public string ServerAddress { get; set; }
        public int ServerPort { get; set; }
        public string Username { get; set; }
        public string DeviceName { get; set; }

        public ServerConfig()
        {
            Mode = ConnectionMode.ClientMode;
            ServerAddress = "192.168.1.100";
            ServerPort = 8585;
            Username = "";
            DeviceName = "Windows Phone";
        }
    }
}
