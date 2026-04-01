namespace GB32960.Protocol.DataTypes;

/// <summary>信息类型 0x03 燃料电池数据</summary>
public class FuelCellData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.FuelCellData;

    public ushort Voltage { get; set; }                          // 0.1V
    public ushort Current { get; set; }                          // 0.1A
    public ushort ConsumptionRate { get; set; }                  // 0.01kg/100km
    public ushort TempProbeCount { get; set; }
    public List<byte> ProbeTemperatures { get; set; } = new();   // 偏移-40℃
    public ushort MaxHydrogenTemp { get; set; }                  // 0.1℃, 偏移-400
    public byte MaxHydrogenTempProbe { get; set; }
    public ushort MaxHydrogenConcentration { get; set; }         // 1mg/kg
    public byte MaxConcentrationSensor { get; set; }
    public ushort HydrogenPressure { get; set; }                 // 0.1MPa
    public byte HighVoltageDCState { get; set; }
}
