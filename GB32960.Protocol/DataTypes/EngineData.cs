namespace GB32960.Protocol.DataTypes;

/// <summary>信息类型 0x04 发动机数据 (5字节)</summary>
public class EngineData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.EngineData;

    public EngineState State { get; set; }
    public ushort CrankshaftRPM { get; set; }            // 1rpm
    public ushort FuelConsumptionRate { get; set; }      // 0.01L/100km
}
