namespace GB32960.Protocol.DataTypes;

/// <summary>信息类型 0x06 极值数据 (14字节)</summary>
public class ExtremeValueData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.ExtremeValueData;

    public byte MaxVoltageSubsystem { get; set; }
    public byte MaxVoltageCellIndex { get; set; }
    public ushort MaxCellVoltage { get; set; }           // 0.001V
    public byte MinVoltageSubsystem { get; set; }
    public byte MinVoltageCellIndex { get; set; }
    public ushort MinCellVoltage { get; set; }           // 0.001V
    public byte MaxTempSubsystem { get; set; }
    public byte MaxTempProbeIndex { get; set; }
    public byte MaxTemperature { get; set; }             // 偏移-40℃
    public byte MinTempSubsystem { get; set; }
    public byte MinTempProbeIndex { get; set; }
    public byte MinTemperature { get; set; }             // 偏移-40℃

    public int GetMaxTempC() => MaxTemperature == 0xFF ? int.MinValue : MaxTemperature - 40;
    public int GetMinTempC() => MinTemperature == 0xFF ? int.MinValue : MinTemperature - 40;
    public double GetMaxVoltageV() => MaxCellVoltage == 0xFFFF ? -1 : MaxCellVoltage / 1000.0;
    public double GetMinVoltageV() => MinCellVoltage == 0xFFFF ? -1 : MinCellVoltage / 1000.0;
}
