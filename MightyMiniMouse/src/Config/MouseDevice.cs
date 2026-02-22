using System.Text.Json.Serialization;

namespace MightyMiniMouse.Config;

public class MouseDevice
{
    public string DeviceId { get; set; } = "";
    public string Nickname { get; set; } = "";
    
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Nickname) ? DeviceId : Nickname;
}
