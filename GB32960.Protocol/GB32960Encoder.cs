using System.Text;

namespace GB32960.Protocol;

public static class GB32960Encoder
{
    public static byte CalculateChecksum(byte[] data, int start, int length)
    {
        byte xor = 0;
        for (int i = start; i < start + length; i++)
            xor ^= data[i];
        return xor;
    }

    /// <summary>编码完整消息帧</summary>
    public static byte[] Encode(CommandType command, ResponseFlag response, string vin,
                                 EncryptionType encryption, byte[] dataUnit)
    {
        var buf = new List<byte>(GB32960Constants.HEADER_LENGTH + dataUnit.Length + 1);

        // 起始符
        buf.Add(GB32960Constants.START_BYTE);
        buf.Add(GB32960Constants.START_BYTE);

        // 命令标识 + 应答标志
        buf.Add((byte)command);
        buf.Add((byte)response);

        // VIN (17字节, 不足右填空格)
        var vinBytes = Encoding.ASCII.GetBytes(vin.PadRight(GB32960Constants.VIN_LENGTH));
        for (int i = 0; i < GB32960Constants.VIN_LENGTH; i++)
            buf.Add(i < vinBytes.Length ? vinBytes[i] : (byte)0x20);

        // 加密方式
        buf.Add((byte)encryption);

        // 数据单元长度 (big-endian)
        ushort dataLen = (ushort)dataUnit.Length;
        buf.Add((byte)(dataLen >> 8));
        buf.Add((byte)(dataLen & 0xFF));

        // 数据单元
        buf.AddRange(dataUnit);

        // 校验码: XOR from index 2 to end
        byte checksum = CalculateChecksum(buf.ToArray(), 2, buf.Count - 2);
        buf.Add(checksum);

        return buf.ToArray();
    }

    /// <summary>通用应答（数据单元为空）</summary>
    public static byte[] EncodeResponse(CommandType command, ResponseFlag response, string vin)
    {
        return Encode(command, response, vin, EncryptionType.None, Array.Empty<byte>());
    }

    /// <summary>车辆登入应答</summary>
    public static byte[] EncodeVehicleLoginResponse(string vin, ResponseFlag result)
    {
        return Encode(CommandType.VehicleLogin, result, vin, EncryptionType.None, Array.Empty<byte>());
    }

    /// <summary>心跳应答</summary>
    public static byte[] EncodeHeartbeatResponse(string vin)
    {
        return Encode(CommandType.Heartbeat, ResponseFlag.Success, vin, EncryptionType.None, Array.Empty<byte>());
    }

    /// <summary>终端校时应答（数据单元=6字节服务器时间）</summary>
    public static byte[] EncodeTimeSyncResponse(string vin)
    {
        var now = DateTime.Now;
        var timeData = new byte[]
        {
            (byte)(now.Year - 2000),
            (byte)now.Month,
            (byte)now.Day,
            (byte)now.Hour,
            (byte)now.Minute,
            (byte)now.Second,
        };
        return Encode(CommandType.TimeSync, ResponseFlag.Success, vin, EncryptionType.None, timeData);
    }

    /// <summary>实时数据/补发数据应答</summary>
    public static byte[] EncodeRealtimeDataResponse(string vin, CommandType cmd)
    {
        return Encode(cmd, ResponseFlag.Success, vin, EncryptionType.None, Array.Empty<byte>());
    }

    /// <summary>查询命令（下行）</summary>
    public static byte[] EncodeQuery(string vin, byte[] queryData)
    {
        return Encode(CommandType.Query, ResponseFlag.Command, vin, EncryptionType.None, queryData);
    }

    /// <summary>设置命令（下行）</summary>
    public static byte[] EncodeSetup(string vin, byte[] setupData)
    {
        return Encode(CommandType.Setup, ResponseFlag.Command, vin, EncryptionType.None, setupData);
    }

    /// <summary>终端控制命令（下行）</summary>
    public static byte[] EncodeControl(string vin, byte[] controlData)
    {
        return Encode(CommandType.Control, ResponseFlag.Command, vin, EncryptionType.None, controlData);
    }
}
