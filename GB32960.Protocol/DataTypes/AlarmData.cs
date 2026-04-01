namespace GB32960.Protocol.DataTypes;

/// <summary>信息类型 0x07 报警数据</summary>
public class AlarmData : IRealtimeInfoItem
{
    public InfoType Type => InfoType.AlarmData;

    public byte MaxAlarmLevel { get; set; }                      // 0=无, 1-3=等级
    public uint GeneralAlarmFlags { get; set; }                  // 32位通用报警标志

    public byte BatteryFaultCount { get; set; }
    public List<uint> BatteryFaultCodes { get; set; } = new();
    public byte MotorFaultCount { get; set; }
    public List<uint> MotorFaultCodes { get; set; } = new();
    public byte EngineFaultCount { get; set; }
    public List<uint> EngineFaultCodes { get; set; } = new();
    public byte OtherFaultCount { get; set; }
    public List<uint> OtherFaultCodes { get; set; } = new();

    // 通用报警位访问
    public bool TempDifferenceAlarm => (GeneralAlarmFlags & (1u << 0)) != 0;
    public bool BatteryHighTempAlarm => (GeneralAlarmFlags & (1u << 1)) != 0;
    public bool BatteryHighVoltageAlarm => (GeneralAlarmFlags & (1u << 2)) != 0;
    public bool BatteryLowVoltageAlarm => (GeneralAlarmFlags & (1u << 3)) != 0;
    public bool SOCLowAlarm => (GeneralAlarmFlags & (1u << 4)) != 0;
    public bool CellOverVoltageAlarm => (GeneralAlarmFlags & (1u << 5)) != 0;
    public bool CellUnderVoltageAlarm => (GeneralAlarmFlags & (1u << 6)) != 0;
    public bool SOCHighAlarm => (GeneralAlarmFlags & (1u << 7)) != 0;
    public bool SOCJumpAlarm => (GeneralAlarmFlags & (1u << 8)) != 0;
    public bool BatteryMismatchAlarm => (GeneralAlarmFlags & (1u << 9)) != 0;
    public bool CellConsistencyAlarm => (GeneralAlarmFlags & (1u << 10)) != 0;
    public bool InsulationAlarm => (GeneralAlarmFlags & (1u << 11)) != 0;
    public bool DcDcTempAlarm => (GeneralAlarmFlags & (1u << 12)) != 0;
    public bool BrakeSystemAlarm => (GeneralAlarmFlags & (1u << 13)) != 0;
    public bool DcDcStatusAlarm => (GeneralAlarmFlags & (1u << 14)) != 0;
    public bool MotorControllerTempAlarm => (GeneralAlarmFlags & (1u << 15)) != 0;
    public bool HighVoltageInterlockAlarm => (GeneralAlarmFlags & (1u << 16)) != 0;
    public bool MotorTempAlarm => (GeneralAlarmFlags & (1u << 17)) != 0;
    public bool ChargingOverTempAlarm => (GeneralAlarmFlags & (1u << 18)) != 0;
}
