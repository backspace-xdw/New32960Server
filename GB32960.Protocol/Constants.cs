namespace GB32960.Protocol;

public static class GB32960Constants
{
    public const byte START_BYTE = 0x23;           // '#'
    public const int HEADER_LENGTH = 24;           // 2(##) + 1(cmd) + 1(resp) + 17(VIN) + 1(enc) + 2(len)
    public const int MIN_MESSAGE_LENGTH = 25;      // header + 1(checksum), data can be 0
    public const int VIN_LENGTH = 17;
    public const int ICCID_LENGTH = 20;
    public const int TIME_LENGTH = 6;              // YY MM DD HH mm ss
}

// 命令标识
public enum CommandType : byte
{
    VehicleLogin      = 0x01,
    RealtimeData      = 0x02,
    SupplementaryData = 0x03,
    VehicleLogout     = 0x04,
    PlatformLogin     = 0x05,
    PlatformLogout    = 0x06,
    Heartbeat         = 0x07,
    TimeSync          = 0x08,
    Query             = 0x80,
    Setup             = 0x81,
    Control           = 0x82,
}

// 应答标志
public enum ResponseFlag : byte
{
    Success      = 0x01,
    Error        = 0x02,
    VinDuplicate = 0x03,
    Command      = 0xFE,
    Invalid      = 0xFF,
}

// 数据加密方式
public enum EncryptionType : byte
{
    None      = 0x01,
    RSA       = 0x02,
    AES128    = 0x03,
    Exception = 0xFE,
    Invalid   = 0xFF,
}

// 信息类型标志
public enum InfoType : byte
{
    VehicleData             = 0x01,
    DriveMotorData          = 0x02,
    FuelCellData            = 0x03,
    EngineData              = 0x04,
    VehiclePositionData     = 0x05,
    ExtremeValueData        = 0x06,
    AlarmData               = 0x07,
    BatteryVoltageData      = 0x08,
    BatteryTemperatureData  = 0x09,
}

// 车辆状态
public enum VehicleStatus : byte
{
    Started   = 0x01,
    Stopped   = 0x02,
    Other     = 0x03,
    Exception = 0xFE,
    Invalid   = 0xFF,
}

// 充电状态
public enum ChargeState : byte
{
    StoppedCharging  = 0x01,
    DrivingCharging  = 0x02,
    NotCharging      = 0x03,
    ChargeComplete   = 0x04,
    Exception        = 0xFE,
    Invalid          = 0xFF,
}

// 运行模式
public enum DriveMode : byte
{
    PureElectric = 0x01,
    Hybrid       = 0x02,
    FuelOnly     = 0x03,
    Exception    = 0xFE,
    Invalid      = 0xFF,
}

// DC-DC状态
public enum DcDcState : byte
{
    Working   = 0x01,
    Off       = 0x02,
    Exception = 0xFE,
    Invalid   = 0xFF,
}

// 驱动电机状态
public enum MotorState : byte
{
    Consuming  = 0x01,
    Generating = 0x02,
    Off        = 0x03,
    Preparing  = 0x04,
    Exception  = 0xFE,
    Invalid    = 0xFF,
}

// 发动机状态
public enum EngineState : byte
{
    Started   = 0x01,
    Stopped   = 0x02,
    Exception = 0xFE,
    Invalid   = 0xFF,
}

// 定位状态
[Flags]
public enum PositionStatus : byte
{
    Valid          = 0x00,
    InvalidPos     = 0x01,
    SouthLatitude  = 0x02,
    WestLongitude  = 0x04,
}
