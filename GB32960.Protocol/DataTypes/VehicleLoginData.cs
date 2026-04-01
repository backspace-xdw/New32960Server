namespace GB32960.Protocol.DataTypes;

public class VehicleLoginData
{
    public DateTime CollectionTime { get; set; }
    public ushort LoginSequence { get; set; }
    public string ICCID { get; set; } = string.Empty;
    public byte SubsystemCount { get; set; }
    public byte SystemCodeLength { get; set; }
    public List<string> SystemCodes { get; set; } = new();
}
