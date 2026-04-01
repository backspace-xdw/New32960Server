using System.Text;
using GB32960.Protocol.DataTypes;

namespace GB32960.Protocol;

public static class GB32960Decoder
{
    public static byte CalculateChecksum(byte[] data, int start, int length)
    {
        byte xor = 0;
        for (int i = start; i < start + length; i++)
            xor ^= data[i];
        return xor;
    }

    /// <summary>解码完整消息帧</summary>
    public static GB32960Message? Decode(byte[] data)
    {
        if (data.Length < GB32960Constants.MIN_MESSAGE_LENGTH) return null;
        if (data[0] != GB32960Constants.START_BYTE || data[1] != GB32960Constants.START_BYTE) return null;

        var msg = new GB32960Message
        {
            Command = (CommandType)data[2],
            Response = (ResponseFlag)data[3],
            VIN = Encoding.ASCII.GetString(data, 4, GB32960Constants.VIN_LENGTH),
            Encryption = (EncryptionType)data[21],
            DataLength = (ushort)((data[22] << 8) | data[23]),
        };

        int expectedLen = GB32960Constants.HEADER_LENGTH + msg.DataLength + 1;
        if (data.Length < expectedLen) return null;

        // 提取数据单元
        if (msg.DataLength > 0)
        {
            msg.Data = new byte[msg.DataLength];
            Array.Copy(data, 24, msg.Data, 0, msg.DataLength);
        }

        // 校验码
        msg.Checksum = data[24 + msg.DataLength];
        byte calc = CalculateChecksum(data, 2, 22 + msg.DataLength); // cmd到data末尾
        if (calc != msg.Checksum) return null;

        return msg;
    }

    // ─── 读取辅助 ─────────────────────────────────

    private static byte ReadByte(byte[] buf, ref int offset) => buf[offset++];

    private static ushort ReadUInt16(byte[] buf, ref int offset)
    {
        ushort v = (ushort)((buf[offset] << 8) | buf[offset + 1]);
        offset += 2;
        return v;
    }

    private static uint ReadUInt32(byte[] buf, ref int offset)
    {
        uint v = ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) |
                 ((uint)buf[offset + 2] << 8) | buf[offset + 3];
        offset += 4;
        return v;
    }

    private static DateTime ReadDateTime(byte[] buf, ref int offset)
    {
        int y = 2000 + buf[offset++];
        int m = buf[offset++];
        int d = buf[offset++];
        int h = buf[offset++];
        int mi = buf[offset++];
        int s = buf[offset++];
        try { return new DateTime(y, Math.Max(1, m), Math.Max(1, d), h, mi, s); }
        catch { return DateTime.MinValue; }
    }

    private static string ReadBCD(byte[] buf, ref int offset, int byteLen)
    {
        var sb = new StringBuilder(byteLen * 2);
        for (int i = 0; i < byteLen; i++)
        {
            sb.Append((buf[offset] >> 4).ToString("X"));
            sb.Append((buf[offset] & 0x0F).ToString("X"));
            offset++;
        }
        return sb.ToString();
    }

    // ─── 车辆登入 0x01 ────────────────────────────

    public static VehicleLoginData? DecodeVehicleLogin(byte[] data)
    {
        if (data.Length < 30) return null; // 6+2+20+1+1 = 30 minimum
        int offset = 0;
        var result = new VehicleLoginData
        {
            CollectionTime = ReadDateTime(data, ref offset),
            LoginSequence = ReadUInt16(data, ref offset),
            ICCID = ReadBCD(data, ref offset, GB32960Constants.ICCID_LENGTH),
            SubsystemCount = ReadByte(data, ref offset),
            SystemCodeLength = ReadByte(data, ref offset),
        };

        for (int i = 0; i < result.SubsystemCount && offset + result.SystemCodeLength <= data.Length; i++)
        {
            var code = Encoding.ASCII.GetString(data, offset, result.SystemCodeLength);
            offset += result.SystemCodeLength;
            result.SystemCodes.Add(code);
        }

        return result;
    }

    // ─── 车辆登出 0x04 ────────────────────────────

    public static VehicleLogoutData? DecodeVehicleLogout(byte[] data)
    {
        if (data.Length < 8) return null;
        int offset = 0;
        return new VehicleLogoutData
        {
            CollectionTime = ReadDateTime(data, ref offset),
            LogoutSequence = ReadUInt16(data, ref offset),
        };
    }

    // ─── 实时/补发数据 0x02/0x03 ──────────────────

    public static (DateTime time, List<IRealtimeInfoItem> items) DecodeRealtimeData(byte[] data)
    {
        var items = new List<IRealtimeInfoItem>();
        if (data.Length < 7) return (DateTime.MinValue, items); // 6(time) + 1(type) minimum

        int offset = 0;
        var time = ReadDateTime(data, ref offset);

        while (offset < data.Length)
        {
            byte typeFlag = data[offset++];
            IRealtimeInfoItem? item = (InfoType)typeFlag switch
            {
                InfoType.VehicleData            => DecodeVehicleData(data, ref offset),
                InfoType.DriveMotorData         => DecodeDriveMotorData(data, ref offset),
                InfoType.FuelCellData           => DecodeFuelCellData(data, ref offset),
                InfoType.EngineData             => DecodeEngineData(data, ref offset),
                InfoType.VehiclePositionData     => DecodeVehiclePositionData(data, ref offset),
                InfoType.ExtremeValueData        => DecodeExtremeValueData(data, ref offset),
                InfoType.AlarmData              => DecodeAlarmData(data, ref offset),
                InfoType.BatteryVoltageData     => DecodeBatteryVoltageData(data, ref offset),
                InfoType.BatteryTemperatureData => DecodeBatteryTemperatureData(data, ref offset),
                _ => null, // 自定义类型 0x80-0xFE，跳过（无法确定长度，中止循环）
            };

            if (item != null) items.Add(item);
            else break; // 遇到未知类型则停止解析
        }

        return (time, items);
    }

    // ─── 信息类型子解码器 ─────────────────────────

    private static VehicleData? DecodeVehicleData(byte[] buf, ref int offset)
    {
        if (offset + 20 > buf.Length) return null;
        return new VehicleData
        {
            Status = (VehicleStatus)ReadByte(buf, ref offset),
            ChargeState = (ChargeState)ReadByte(buf, ref offset),
            DriveMode = (DriveMode)ReadByte(buf, ref offset),
            Speed = ReadUInt16(buf, ref offset),
            Mileage = ReadUInt32(buf, ref offset),
            TotalVoltage = ReadUInt16(buf, ref offset),
            TotalCurrent = ReadUInt16(buf, ref offset),
            SOC = ReadByte(buf, ref offset),
            DcDcStatus = (DcDcState)ReadByte(buf, ref offset),
            Gear = ReadByte(buf, ref offset),
            InsulationResistance = ReadUInt16(buf, ref offset),
            AcceleratorPedal = ReadByte(buf, ref offset),
            BrakePedal = ReadByte(buf, ref offset),
        };
    }

    private static DriveMotorData? DecodeDriveMotorData(byte[] buf, ref int offset)
    {
        if (offset + 1 > buf.Length) return null;
        var result = new DriveMotorData { MotorCount = ReadByte(buf, ref offset) };
        for (int i = 0; i < result.MotorCount && offset + 12 <= buf.Length; i++)
        {
            result.Motors.Add(new MotorInfo
            {
                Sequence = ReadByte(buf, ref offset),
                State = (MotorState)ReadByte(buf, ref offset),
                ControllerTemp = ReadByte(buf, ref offset),
                RPM = ReadUInt16(buf, ref offset),
                Torque = ReadUInt16(buf, ref offset),
                MotorTemp = ReadByte(buf, ref offset),
                ControllerVoltage = ReadUInt16(buf, ref offset),
                ControllerCurrent = ReadUInt16(buf, ref offset),
            });
        }
        return result;
    }

    private static FuelCellData? DecodeFuelCellData(byte[] buf, ref int offset)
    {
        if (offset + 8 > buf.Length) return null;
        var result = new FuelCellData
        {
            Voltage = ReadUInt16(buf, ref offset),
            Current = ReadUInt16(buf, ref offset),
            ConsumptionRate = ReadUInt16(buf, ref offset),
            TempProbeCount = ReadUInt16(buf, ref offset),
        };
        for (int i = 0; i < result.TempProbeCount && offset < buf.Length; i++)
            result.ProbeTemperatures.Add(ReadByte(buf, ref offset));

        if (offset + 8 <= buf.Length)
        {
            result.MaxHydrogenTemp = ReadUInt16(buf, ref offset);
            result.MaxHydrogenTempProbe = ReadByte(buf, ref offset);
            result.MaxHydrogenConcentration = ReadUInt16(buf, ref offset);
            result.MaxConcentrationSensor = ReadByte(buf, ref offset);
            result.HydrogenPressure = ReadUInt16(buf, ref offset);
        }
        if (offset < buf.Length)
            result.HighVoltageDCState = ReadByte(buf, ref offset);

        return result;
    }

    private static EngineData? DecodeEngineData(byte[] buf, ref int offset)
    {
        if (offset + 5 > buf.Length) return null;
        return new EngineData
        {
            State = (EngineState)ReadByte(buf, ref offset),
            CrankshaftRPM = ReadUInt16(buf, ref offset),
            FuelConsumptionRate = ReadUInt16(buf, ref offset),
        };
    }

    private static VehiclePositionData? DecodeVehiclePositionData(byte[] buf, ref int offset)
    {
        if (offset + 9 > buf.Length) return null;
        return new VehiclePositionData
        {
            StatusFlags = (PositionStatus)ReadByte(buf, ref offset),
            Longitude = ReadUInt32(buf, ref offset),
            Latitude = ReadUInt32(buf, ref offset),
        };
    }

    private static ExtremeValueData? DecodeExtremeValueData(byte[] buf, ref int offset)
    {
        if (offset + 14 > buf.Length) return null;
        return new ExtremeValueData
        {
            MaxVoltageSubsystem = ReadByte(buf, ref offset),
            MaxVoltageCellIndex = ReadByte(buf, ref offset),
            MaxCellVoltage = ReadUInt16(buf, ref offset),
            MinVoltageSubsystem = ReadByte(buf, ref offset),
            MinVoltageCellIndex = ReadByte(buf, ref offset),
            MinCellVoltage = ReadUInt16(buf, ref offset),
            MaxTempSubsystem = ReadByte(buf, ref offset),
            MaxTempProbeIndex = ReadByte(buf, ref offset),
            MaxTemperature = ReadByte(buf, ref offset),
            MinTempSubsystem = ReadByte(buf, ref offset),
            MinTempProbeIndex = ReadByte(buf, ref offset),
            MinTemperature = ReadByte(buf, ref offset),
        };
    }

    private static AlarmData? DecodeAlarmData(byte[] buf, ref int offset)
    {
        if (offset + 9 > buf.Length) return null; // 1+4+4*1 minimum
        var result = new AlarmData
        {
            MaxAlarmLevel = ReadByte(buf, ref offset),
            GeneralAlarmFlags = ReadUInt32(buf, ref offset),
        };

        // 可充电储能装置故障
        if (offset < buf.Length)
        {
            result.BatteryFaultCount = ReadByte(buf, ref offset);
            for (int i = 0; i < result.BatteryFaultCount && offset + 4 <= buf.Length; i++)
                result.BatteryFaultCodes.Add(ReadUInt32(buf, ref offset));
        }
        // 驱动电机故障
        if (offset < buf.Length)
        {
            result.MotorFaultCount = ReadByte(buf, ref offset);
            for (int i = 0; i < result.MotorFaultCount && offset + 4 <= buf.Length; i++)
                result.MotorFaultCodes.Add(ReadUInt32(buf, ref offset));
        }
        // 发动机故障
        if (offset < buf.Length)
        {
            result.EngineFaultCount = ReadByte(buf, ref offset);
            for (int i = 0; i < result.EngineFaultCount && offset + 4 <= buf.Length; i++)
                result.EngineFaultCodes.Add(ReadUInt32(buf, ref offset));
        }
        // 其他故障
        if (offset < buf.Length)
        {
            result.OtherFaultCount = ReadByte(buf, ref offset);
            for (int i = 0; i < result.OtherFaultCount && offset + 4 <= buf.Length; i++)
                result.OtherFaultCodes.Add(ReadUInt32(buf, ref offset));
        }

        return result;
    }

    private static BatteryVoltageData? DecodeBatteryVoltageData(byte[] buf, ref int offset)
    {
        if (offset + 1 > buf.Length) return null;
        var result = new BatteryVoltageData { SubsystemCount = ReadByte(buf, ref offset) };
        for (int i = 0; i < result.SubsystemCount && offset + 10 <= buf.Length; i++)
        {
            var sub = new BatterySubsystemVoltage
            {
                SubsystemNumber = ReadByte(buf, ref offset),
                SubsystemVoltage = ReadUInt16(buf, ref offset),
                SubsystemCurrent = ReadUInt16(buf, ref offset),
                TotalCellCount = ReadUInt16(buf, ref offset),
                FrameStartCellIndex = ReadUInt16(buf, ref offset),
                FrameCellCount = ReadByte(buf, ref offset),
            };
            for (int j = 0; j < sub.FrameCellCount && offset + 2 <= buf.Length; j++)
                sub.CellVoltages.Add(ReadUInt16(buf, ref offset));
            result.Subsystems.Add(sub);
        }
        return result;
    }

    private static BatteryTemperatureData? DecodeBatteryTemperatureData(byte[] buf, ref int offset)
    {
        if (offset + 1 > buf.Length) return null;
        var result = new BatteryTemperatureData { SubsystemCount = ReadByte(buf, ref offset) };
        for (int i = 0; i < result.SubsystemCount && offset + 3 <= buf.Length; i++)
        {
            var sub = new BatterySubsystemTemperature
            {
                SubsystemNumber = ReadByte(buf, ref offset),
                ProbeCount = ReadUInt16(buf, ref offset),
            };
            for (int j = 0; j < sub.ProbeCount && offset < buf.Length; j++)
                sub.ProbeTemperatures.Add(ReadByte(buf, ref offset));
            result.Subsystems.Add(sub);
        }
        return result;
    }
}
