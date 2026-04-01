namespace GB32960.Protocol.DataTypes;

public class BatterySubsystemTemperature
{
    public byte SubsystemNumber { get; set; }
    public ushort ProbeCount { get; set; }
    public List<byte> ProbeTemperatures { get; set; } = new(); // 偏移-40℃
}

/// <summary>信息类型 0x09 可充电储能装置温度数据</summary>
public class BatteryTemperatureData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.BatteryTemperatureData;
    public byte SubsystemCount { get; set; }
    public List<BatterySubsystemTemperature> Subsystems { get; set; } = new();
}
