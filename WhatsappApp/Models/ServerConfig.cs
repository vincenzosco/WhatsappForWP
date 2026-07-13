namespace WhatsappApp.Models
{
    public enum ConnectionMode
    {
        ClientMode,
        ServerMode
    }

    public class ServerConfig
    {
        public ConnectionMode Mode { get; set; } = ConnectionMode.ClientMode;
        public string ServerAddress { get; set; } = "192.168.1.100";
        public int ServerPort { get; set; } = 8585;
        public string Username { get; set; } = "";
        public string DeviceName { get; set; } = "Windows Phone";
    }
}
