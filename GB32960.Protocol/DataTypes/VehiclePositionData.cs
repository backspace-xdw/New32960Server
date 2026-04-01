namespace GB32960.Protocol.DataTypes;

/// <summary>信息类型 0x05 车辆位置数据 (9字节)</summary>
public class VehiclePositionData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.VehiclePositionData;

    public PositionStatus StatusFlags { get; set; }
    public uint Longitude { get; set; }                  // 1e-6度
    public uint Latitude { get; set; }                   // 1e-6度

    public bool IsValid => (StatusFlags & PositionStatus.InvalidPos) == 0;
    public double GetLongitude() => (IsWest ? -1.0 : 1.0) * Longitude / 1_000_000.0;
    public double GetLatitude() => (IsSouth ? -1.0 : 1.0) * Latitude / 1_000_000.0;
    private bool IsSouth => (StatusFlags & PositionStatus.SouthLatitude) != 0;
    private bool IsWest => (StatusFlags & PositionStatus.WestLongitude) != 0;
}
