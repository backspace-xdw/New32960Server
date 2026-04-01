# New32960Server

GB/T 32960.3-2016 新能源汽车远程监测服务端，C# .NET 9.0 实现，支持 100,000+ 并发终端连接。

## 功能特性

- 完整实现 GB/T 32960.3-2016 协议
- 11 种命令类型全覆盖
- 9 种实时信息体解析
- 高性能 SocketAsyncEventArgs + 对象池
- ArrayPool 缓冲区复用
- 异步消息发送
- VIN 会话管理 + 超时清理
- 按命令类型统计
- 彩色控制台 UI + 自动刷新模式

## 协议支持

### 命令类型

| CMD | 名称 | 方向 |
|-----|------|------|
| 0x01 | 车辆登入 | 上行 |
| 0x02 | 实时信息上报 | 上行 |
| 0x03 | 补发信息上报 | 上行 |
| 0x04 | 车辆登出 | 上行 |
| 0x05 | 平台登入 | 上行 |
| 0x06 | 平台登出 | 上行 |
| 0x07 | 心跳 | 上行 |
| 0x08 | 终端校时 | 上行 |
| 0x80 | 查询命令 | 下行 |
| 0x81 | 设置命令 | 下行 |
| 0x82 | 终端控制 | 下行 |

### 实时信息体

| 类型 | 名称 |
|------|------|
| 0x01 | 整车数据（车速/里程/电压/电流/SOC等） |
| 0x02 | 驱动电机数据 |
| 0x03 | 燃料电池数据 |
| 0x04 | 发动机数据 |
| 0x05 | 车辆位置数据（经纬度） |
| 0x06 | 极值数据（电压/温度极值） |
| 0x07 | 报警数据（19种通用报警 + 故障码） |
| 0x08 | 可充电储能装置电压数据 |
| 0x09 | 可充电储能装置温度数据 |

## 项目结构

```
New32960Server/
├── GB32960.Protocol/          # 协议库（零外部依赖）
│   ├── Constants.cs           # 枚举常量
│   ├── GB32960Message.cs      # 消息结构
│   ├── GB32960Decoder.cs      # 解码器
│   ├── GB32960Encoder.cs      # 编码器
│   ├── GB32960MessageBuffer.cs # TCP粘包处理
│   └── DataTypes/             # 9种信息体 + 登入登出模型
├── GB32960.Server/            # 服务端
│   ├── GB32960TcpServer.cs    # TCP服务器核心
│   ├── SessionManager.cs      # 会话管理
│   ├── ServerConfig.cs        # 配置
│   └── Program.cs             # 入口 + 控制台UI
└── GB32960.TestClient/        # 测试客户端
    └── Program.cs             # 单车测试 + 并发压测
```

## 快速开始

```bash
# 编译
dotnet build

# 启动服务端（默认端口 32960）
dotnet run --project GB32960.Server

# 启动测试客户端
dotnet run --project GB32960.TestClient
```

## 配置

编辑 `GB32960.Server/appsettings.json`：

```json
{
  "ServerConfig": {
    "IpAddress": "0.0.0.0",
    "Port": 32960,
    "MaxConnections": 100000,
    "SessionTimeoutMinutes": 30,
    "ReceiveBufferSize": 4096,
    "LogLevel": "Information"
  }
}
```

## 控制台操作

| 按键 | 功能 |
|------|------|
| S | 显示统计（在线数/消息数/吞吐量/命令分布） |
| A | 开关自动刷新（5秒间隔） |
| L | 显示终端列表 |
| Q | 退出 |

## 技术栈

- .NET 9.0 / C# 13
- SocketAsyncEventArgs 异步 TCP
- System.Buffers.ArrayPool 缓冲复用
- ConcurrentDictionary 并发会话管理
- Microsoft.Extensions.Logging + Configuration
