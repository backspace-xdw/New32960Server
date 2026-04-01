namespace GB32960.Protocol.DataTypes;

/// <summary>信息类型 0x01 整车数据 (20字节)</summary>
public class VehicleData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.VehicleData;

    public VehicleStatus Status { get; set; }
    public ChargeState ChargeState { get; set; }
    public DriveMode DriveMode { get; set; }
    public ushort Speed { get; set; }                    // 0.1km/h, 0xFFFF=无效
    public uint Mileage { get; set; }                    // 0.1km, 0xFFFFFFFF=无效
    public ushort TotalVoltage { get; set; }             // 0.1V
    public ushort TotalCurrent { get; set; }             // 0.1A, 偏移-1000A
    public byte SOC { get; set; }                        // 1%, 0xFF=无效
    public DcDcState DcDcStatus { get; set; }
    public byte Gear { get; set; }                       // 档位
    public ushort InsulationResistance { get; set; }     // 1kΩ
    public byte AcceleratorPedal { get; set; }           // 0-100%
    public byte BrakePedal { get; set; }

    public double GetSpeedKmh() => Speed == 0xFFFF ? -1 : Speed / 10.0;
    public double GetMileageKm() => Mileage == 0xFFFFFFFF ? -1 : Mileage / 10.0;
    public double GetVoltageV() => TotalVoltage == 0xFFFF ? -1 : TotalVoltage / 10.0;
    public double GetCurrentA() => TotalCurrent == 0xFFFF ? -1 : (TotalCurrent - 10000) / 10.0;
}
