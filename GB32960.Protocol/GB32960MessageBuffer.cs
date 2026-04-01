namespace GB32960.Protocol;

/// <summary>
/// TCP粘包/半包处理 — 环形缓冲区实现
/// 避免 List&lt;byte&gt;.RemoveRange 的 O(n) 内存移动
/// </summary>
public class GB32960MessageBuffer
{
    private byte[] _buffer;
    private int _head;  // 有效数据起始
    private int _tail;  // 有效数据末尾
    private readonly object _lock = new();

    public GB32960MessageBuffer(int capacity = 8192)
    {
        _buffer = new byte[capacity];
    }

    public int DataLength
    {
        get { lock (_lock) return _tail - _head; }
    }

    public void Append(byte[] data, int offset, int count)
    {
        lock (_lock)
        {
            EnsureCapacity(count);
            Buffer.BlockCopy(data, offset, _buffer, _tail, count);
            _tail += count;
        }
    }

    public List<byte[]> ExtractMessages()
    {
        var messages = new List<byte[]>();

        lock (_lock)
        {
            while (true)
            {
                int available = _tail - _head;

                // 查找 ## 起始符
                int startIndex = -1;
                for (int i = _head; i < _tail - 1; i++)
                {
                    if (_buffer[i] == GB32960Constants.START_BYTE &&
                        _buffer[i + 1] == GB32960Constants.START_BYTE)
                    {
                        startIndex = i;
                        break;
                    }
                }

                if (startIndex < 0)
                {
                    // 没找到起始符，丢弃全部
                    if (available > 1) _head = _tail - 1;
                    break;
                }

                // 丢弃起始符之前的数据
                _head = startIndex;
                available = _tail - _head;

                // 需要完整头部
                if (available < GB32960Constants.HEADER_LENGTH)
                    break;

                // 读取数据长度
                ushort dataLength = (ushort)((_buffer[_head + 22] << 8) | _buffer[_head + 23]);
                int totalLength = GB32960Constants.HEADER_LENGTH + dataLength + 1;

                if (available < totalLength)
                    break;

                // 提取完整消息（零拷贝提取）
                var messageBytes = new byte[totalLength];
                Buffer.BlockCopy(_buffer, _head, messageBytes, 0, totalLength);
                _head += totalLength;

                messages.Add(messageBytes);
            }

            // 压缩：将剩余数据移到缓冲区头部
            Compact();
        }

        return messages;
    }

    private void EnsureCapacity(int additionalBytes)
    {
        if (_tail + additionalBytes <= _buffer.Length)
            return;

        // 先尝试压缩
        if (_head > 0)
        {
            Compact();
            if (_tail + additionalBytes <= _buffer.Length)
                return;
        }

        // 扩容
        int newSize = Math.Max(_buffer.Length * 2, _tail + additionalBytes);
        var newBuffer = new byte[newSize];
        int dataLen = _tail - _head;
        if (dataLen > 0)
            Buffer.BlockCopy(_buffer, _head, newBuffer, 0, dataLen);
        _buffer = newBuffer;
        _head = 0;
        _tail = dataLen;
    }

    private void Compact()
    {
        int dataLen = _tail - _head;
        if (_head == 0 || dataLen == 0)
        {
            if (dataLen == 0) { _head = 0; _tail = 0; }
            return;
        }
        Buffer.BlockCopy(_buffer, _head, _buffer, 0, dataLen);
        _head = 0;
        _tail = dataLen;
    }

    public void Clear()
    {
        lock (_lock) { _head = 0; _tail = 0; }
    }
}
