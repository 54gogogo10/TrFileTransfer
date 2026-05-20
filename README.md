# TrFileTransfer

局域网文件传输工具，带 SHA-256 完整性校验。Windows GUI（WinForms）。

## 功能

- **TCP** — 面向连接，防火墙友好
- **UDP** — Go-Back-N ARQ 可靠传输（滑动窗口、重传、选择性 NAK、SHA-256）
- **单文件**或**文件夹**（递归，保留目录结构）
- **多客户端并发** — 服务端同时接受多个客户端传输，每个独立进度卡片
- **配置持久化** — 地址、端口、语言等设置自动保存到 `%AppData%`
- **English / 中文** 可切换 UI
- 收发进度面板左右分列，每个传输独立进度卡片（进度条 + 速度），完成后自动移除

## 快速开始

### 环境要求

- Windows 7 SP1 或更高版本
- .NET Framework 4.5 或更高版本（推荐 4.6.1+）

### 构建

```
cd TrFileTransfer
build.bat
```

生成 `TrFileTransfer.exe`。无需 Visual Studio——使用内置 `csc.exe` 编译器。

### 使用

1. **接收方** — 左侧服务器面板：选择 TCP 或 UDP，选择保存目录，点击*启动服务器*
2. **发送方** — 右侧客户端面板：输入服务器 IP，浏览文件或文件夹，点击*发送*
3. 勾选*文件夹模式*可发送整个目录
4. 勾选*监控模式*可持续监控目录——启动时先扫描已有文件并入队，然后检测新文件自动发送

## 协议

完整 TCP 和 UDP 线协议规范参见 [WIRE-PROTOCOL.md](WIRE-PROTOCOL.md)。

## 架构

| 文件 | 职责 |
|------|------|
| `Program.cs` | 入口 |
| `MainForm.cs` | WinForms UI（代码构建，无设计器） |
| `TransferClient.cs` | TCP 发送端 |
| `TransferServer.cs` | TCP 接收端 |
| `TransferUdpClient.cs` | 可靠 UDP 发送端（Go-Back-N） |
| `TransferUdpServer.cs` | 可靠 UDP 接收端，分发到各 Session |
| `TransferUdpSession.cs` | 每客户端 UDP 传输会话（包入队、写缓冲、NAK） |
| `UdpProtocol.cs` | UDP 线协议常量和包辅助方法 |
| `Config.cs` | 键值配置持久化（`%AppData%\TrFileTransfer\`） |
| `Shared.cs` | `TransferProgress`、`Utils`、`FileEntry` |
| `L10N.cs` | 中英文字符串 |

## 许可证

MIT
