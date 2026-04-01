namespace GB32960.Protocol;

/// <summary>
/// TCP粘包/半包处理缓冲区
/// 基于"##"起始符 + 长度字段的帧识别
/// </summary>
public class GB32960MessageBuffer
{
    private readonly List<byte> _buffer = new();
    private readonly object _lock = new();

    public int BufferSize
    {
        get { lock (_lock) return _buffer.Count; }
    }

    public void Append(byte[] data, int offset, int count)
    {
        lock (_lock)
        {
            for (int i = offset; i < offset + count; i++)
                _buffer.Add(data[i]);
        }
    }

    public List<byte[]> ExtractMessages()
    {
        var messages = new List<byte[]>();

        lock (_lock)
        {
            while (true)
            {
                // 查找 ## 起始符
                int startIndex = -1;
                for (int i = 0; i < _buffer.Count - 1; i++)
                {
                    if (_buffer[i] == GB32960Constants.START_BYTE &&
                        _buffer[i + 1] == GB32960Constants.START_BYTE)
                    {
                        startIndex = i;
                        break;
                    }
                }

                if (startIndex < 0) break;

                // 丢弃起始符之前的垃圾数据
                if (startIndex > 0)
                {
                    _buffer.RemoveRange(0, startIndex);
                    startIndex = 0;
                }

                // 检查是否有完整的头部 (24字节)
                if (_buffer.Count < GB32960Constants.HEADER_LENGTH)
                    break;

                // 读取数据单元长度 (offset 22-23, big-endian)
                ushort dataLength = (ushort)((_buffer[22] << 8) | _buffer[23]);
                int totalLength = GB32960Constants.HEADER_LENGTH + dataLength + 1; // +1 for checksum

                // 检查是否有完整消息
                if (_buffer.Count < totalLength)
                    break;

                // 提取完整消息
                var messageBytes = new byte[totalLength];
                for (int i = 0; i < totalLength; i++)
                    messageBytes[i] = _buffer[i];

                _buffer.RemoveRange(0, totalLength);
                messages.Add(messageBytes);
            }
        }

        return messages;
    }

    public void Clear()
    {
        lock (_lock) _buffer.Clear();
    }
}
