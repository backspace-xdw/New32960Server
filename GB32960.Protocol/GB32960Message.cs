namespace GB32960.Protocol;

public class GB32960Message
{
    public CommandType Command { get; set; }
    public ResponseFlag Response { get; set; }
    public string VIN { get; set; } = string.Empty;
    public EncryptionType Encryption { get; set; }
    public ushort DataLength { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public byte Checksum { get; set; }
}
