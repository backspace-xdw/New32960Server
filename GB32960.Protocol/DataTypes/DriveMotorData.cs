namespace GB32960.Protocol.DataTypes;

/// <summary>单个电机信息 (12字节)</summary>
public class MotorInfo
{
    public byte Sequence { get; set; }
    public MotorState State { get; set; }
    public byte ControllerTemp { get; set; }             // 偏移-40℃, 0xFF=无效
    public ushort RPM { get; set; }                      // 偏移-20000rpm, 0xFFFF=无效
    public ushort Torque { get; set; }                   // 0.1Nm, 偏移-20000, 0xFFFF=无效
    public byte MotorTemp { get; set; }                  // 偏移-40℃
    public ushort ControllerVoltage { get; set; }        // 0.1V
    public ushort ControllerCurrent { get; set; }        // 0.1A, 偏移-10000

    public int GetRPM() => RPM == 0xFFFF ? int.MinValue : RPM - 20000;
    public int GetControllerTempC() => ControllerTemp == 0xFF ? int.MinValue : ControllerTemp - 40;
    public int GetMotorTempC() => MotorTemp == 0xFF ? int.MinValue : MotorTemp - 40;
}

/// <summary>信息类型 0x02 驱动电机数据</summary>
public class DriveMotorData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.DriveMotorData;
    public byte MotorCount { get; set; }
    public List<MotorInfo> Motors { get; set; } = new();
}
