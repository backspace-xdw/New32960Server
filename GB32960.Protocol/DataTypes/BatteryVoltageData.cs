namespace GB32960.Protocol.DataTypes;

public class BatterySubsystemVoltage
{
    public byte SubsystemNumber { get; set; }
    public ushort SubsystemVoltage { get; set; }         // 0.1V
    public ushort SubsystemCurrent { get; set; }         // 0.1A, 偏移-10000
    public ushort TotalCellCount { get; set; }
    public ushort FrameStartCellIndex { get; set; }
    public byte FrameCellCount { get; set; }
    public List<ushort> CellVoltages { get; set; } = new(); // 0.001V each
}

/// <summary>信息类型 0x08 可充电储能装置电压数据</summary>
public class BatteryVoltageData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.BatteryVoltageData;
    public byte SubsystemCount { get; set; }
    public List<BatterySubsystemVoltage> Subsystems { get; set; } = new();
}
