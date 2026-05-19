# CLAUDE.md

本文件为 Claude Code 在此仓库中工作时提供指引。

## 构建

```
# 在 TrFileTransfer/ 目录下运行：
build.bat
```

使用已安装 .NET Framework 4.x 自带的 `csc.exe` 生成 `TrFileTransfer.exe`。引用：`System.dll`、`System.Core.dll`、`System.Windows.Forms.dll`、`System.Drawing.dll`。无需 Visual Studio 或 MSBuild，只需 .NET Framework 运行时（包含 `csc.exe`）。

编译器为 C# 5（内置 `csc.exe` 支持的最高语言版本），生成的 exe 运行在 .NET Framework 4.5+（推荐 4.6.1）。避免 C# 6+ 语法：禁止字符串插值（`$`）、禁止表达式体成员、禁止空条件运算符（`?.`）、禁止异常过滤器（`when`）、禁止数字分隔符。

## 架构

十个源文件编译为单个 WinForms exe：

- **Program.cs** — 入口。`[STAThread]` Main 启动 `MainForm`。
- **MainForm.cs** — GUI。模式选择（服务器/客户端）、协议选择（TCP/UDP）、服务器绑定地址下拉框（从活动网卡获取）、文件夹模式复选框（仅客户端）、进度条、速度/ETA 显示、滚动日志。所有 UI 由代码构建（无设计器）。右上角语言下拉框。日志上限 500 条，进度事件节流约 100ms。事件绑定抽取到 `WireClientEvents`/`WireUdpClientEvents` 辅助方法。服务器完成传输后 UI 保持"监听中..."状态（不重新启用所有输入控件）——服务器仍在运行并接受新传输。`ResetClientUI` 重置客户端控件和状态文本为"就绪"。**监控模式**：`_chkMonitor` 复选框启用文件夹监控——`FileSystemWatcher` 检测新文件，等待文件稳定（连续 2 次 500ms 轮询文件大小不变），通过 TCP 或 UDP 发送，成功后移入"已发送文件"子目录。2 分钟内未稳定的文件移到队列末尾。使用 `TaskCompletionSource<bool>` 跟踪每个文件的完成状态而不重置 UI。详见下方"监控模式"章节。
- **TransferServer.cs** — 异步 TCP 服务器。绑定到可配置地址+端口，一次接受一个连接。`HandleClient` 读取 1 字节类型前缀，分发到 `HandleFileTransfer`（单文件）或 `HandleFolderTransfer`（文件夹，带每个文件的相对路径和 SHA256）。使用 `Task.Factory.StartNew(LongRunning)` 执行接受循环。设置 `NoDelay = true` 和 256KB 缓冲区。
- **TransferClient.cs** — 异步 TCP 客户端。`SendAsync()` 发送单文件，`SendFolderAsync()` 发送文件夹。两者委托到共享的 `RunTransfer` 包装器进行生命周期管理。发送文件元数据 + 数据 + 每个文件的 SHA256 哈希。设置 `NoDelay = true` 和 256KB 缓冲区。`SendFilePayload` 辅助方法由单文件和文件夹传输共享。`SendFileInternal` 和 `SendFolderInternal` 均在成功时触发 `OnTransferComplete`。
- **TransferUdpServer.cs** — 可靠 UDP 服务器。始终监听所有接口（无绑定地址参数——与 TCP 服务器不同，构造函数只接受 `port` 和 `saveDirectory`）。`HandleTransfer` 解析 HELLO 正文类型字节：`0x00` = 单文件（清理文件名），`0x01` = 文件夹文件（保留相对路径，通过 `Utils.SanitizeRelativePath` 创建子目录）。Go-Back-N ARQ。内部接收循环运行直到 FIN 被处理（不仅是所有数据块到达），确保哈希验证完成且 FIN_ACK 已发送才返回。如果在循环中收到新的 HELLO 或 FolderEnd 包，保存到 `_pendingPacketData`/`_pendingPacketEp`，由 `ReceiveLoop` 在下一次迭代时处理——不会静默丢弃任何包。4 MB 套接字缓冲区。关键行为：(a) `retryCount` 在收到**任何**数据包时重置（有序或乱序均可），而不仅是有序；(b) 接收超时时重新发送最后一个累计 ACK，帮助客户端从 ACK 丢失中恢复；(c) 如果 FIN 在数据未收齐时到达，发送 ACK(last received) 而非忽略——告知客户端从何处重传。服务器使用 2 MB 写缓冲（每窗口刷新一次，而非每块）和批量 ACK（每 512 块或 50ms 定时器发送一次 ACK），最小化异步 I/O 调用。数据在 FIN_ACK 前刷新到磁盘以确保安全。
- **TransferUdpClient.cs** — 可靠 UDP 客户端。`SendAsync()` 发送单文件，`SendFolderAsync()` 发送文件夹。两者委托到共享的 `RunUdpTransfer` 包装器。使用滑动窗口（512 块 × 4096 字节 = 2 MB 窗口）、超时重传、丢包时 Go-Back-N。`WaitForAckAsync()` 清空过期非 ACK 包，收到 ACK(seq=0) 后返回。`SendUdpFileDataAsync()` 通过 `Task.WhenAll` 异步发送整个窗口（批量发送，每窗 1 次 await）。1 MB 读缓冲区批量读取文件 I/O。NAK 触发的重传是选择性的（单个块入队，非整个窗口 Go-Back-N）；超时仍触发 Go-Back-N。SHA256 哈希计算一次并在重传中复用。`reportProgress` 参数在文件夹传输时抑制每个文件的进度事件。`CreateUdpClient()` 辅助方法消除 UdpClient 设置重复。所有异步方法使用 `ConfigureAwait(false)` 避免 UI 线程饥饿。
- **UdpProtocol.cs** — 共享的 UDP 线协议常量和辅助方法。此处定义包格式和类型。`BuildPacketFromBuffer` 从源缓冲区+偏移直接构建包，避免中间正文拷贝；`BuildPacket` 委托给它。
- **Config.cs** — 键值配置持久化。保存/加载到 `%AppData%\TrFileTransfer\config.ini`。`Get`/`GetInt`/`GetBool`/`Set`/`SetInt`/`SetBool` 辅助方法。启动时加载，关闭时保存。
- **Shared.cs** — `TransferProgress` 类、`Utils` 静态辅助方法（`FormatSize`、`ConstantTimeEquals`、`LogTo`、`SanitizeRelativePath`、`GetUniqueSavePath`）。四个传输文件通用，避免重复。
- **L10N.cs** — 本地化字符串。静态 `L` 类根据 `L.IsChinese` 返回英文或中文文本。命名规则见下方"本地化约定"。

### 本地化约定

GUI 有语言下拉框（English / 中文），设置 `L.IsChinese`。所有本地化字符串在 `L10N.cs` 中定义。按消费者命名：
- `L.S_*` — TCP 服务器日志消息
- `L.C_*` — TCP 客户端日志消息
- `L.UdpS_*` — UDP 服务器日志消息
- `L.UdpC_*` — UDP 客户端日志消息
- 其他 `L.*` 属性 — MainForm UI 字符串

新增日志消息时遵循以上命名约定添加到 `L10N.cs`。禁止使用内联 `L.IsChinese ? "..." : "..."` 三元表达式。语言切换时 `PopulateBindAddresses()` 重新读取网卡并更新第一个下拉项标签。传输过程中语言切换被禁用（下拉框不可用）。

### 文件名冲突处理

TCP 和 UDP 服务器都使用 `Utils.GetUniqueSavePath`，在基础文件名（扩展名前）追加 `_1`、`_2` 等后缀，解决与保存目录中已有文件/目录的名称冲突。

### 线程安全模式

四个传输类都使用 `volatile bool _isRunning` 标志和 `CancellationTokenSource`。UI 事件处理器通过 `MainForm.cs` 中的 `this.Invoke()` 封送。触发事件前使用局部变量快照模式：`var handler = OnXxx; if (handler != null) handler(...);`。

## TCP 线协议

所有传输以 1 字节类型开始：`0x00` = 单文件，`0x01` = 文件夹。

### 单文件（类型 0x00）

```
[1 byte:  0x00]
[8 bytes: Int64 文件大小，小端序]
[4 bytes: Int32 文件名长度]
[N bytes: UTF-8 文件名]
[M bytes: 文件内容（fileSize 字节）]
[32 bytes: 文件内容 SHA256 哈希]
```

### 文件夹（类型 0x01）

```
[1 byte:  0x01]
[2 bytes: Int16 文件夹名长度]
[N bytes: UTF-8 文件夹名]
[4 bytes: Int32 文件数量]
每个文件：
    [8 bytes: Int64 文件大小]
    [2 bytes: Int16 相对路径长度]
    [N bytes: UTF-8 相对路径（相对于文件夹根目录，如 "subdir/file.txt"）]
    [M bytes: 文件内容（fileSize 字节）]
    [32 bytes: 此文件内容 SHA256 哈希]
```

服务器根据需要从每个文件的相对路径在保存路径中创建子目录。如果某个文件的 SHA256 失败，整个文件夹传输中止。文件名冲突处理在文件夹保存目录名追加 `_1`、`_2`。

## UDP 线协议

包头部（14 字节）：`magic(4) + type(1) + reserved(1) + seq(4) + bodyLen(4)`，其中 magic = `0x55445054`。

| 类型 | 值 | 正文 |
|---|---|---|
| HELLO | 0 | transferType(1) + fileSize(8) + nameLen(2) + fileName(N) |
| DATA | 1 | 文件块（≤ 4096 字节） |
| ACK | 2 | 空（累计确认：最高连续收到的 seq） |
| FIN | 3 | SHA256 哈希（32 字节） |
| FIN_ACK | 4 | 空 |
| FOLDER_END | 5 | 空 |
| NAK | 6 | 空（seq = 首个缺失块；客户端选择性重传该块） |

NAK 流程：当服务器检测到缺口（收到 seq > expectedSeq 的乱序数据）时，对同一 `expectedSeq` 的乱序包进行计数。仅在 `NakThreshold`（3）次乱序到达后才发送 NAK——单次乱序包视为顺序调换，非丢包（类似 TCP 快速重传的 3 个重复 ACK）。NAK seq = `expectedSeq`（首个缺失块）。客户端将**选择性重传**该块入队（非 Go-Back-N），仅重传 NAK 指定的块。同一 seq 的重复 NAK 通过 `lastNakSeq` 抑制。`lastNakSeq` 和 `outOfOrderCount` 在有序数据到达、缺口移动时重置。

HELLO 正文 transferType：`0x00` = 单文件（服务器用 `Path.GetFileName` 清理文件名），`0x01` = 文件夹文件（服务器保留相对路径，创建子目录）。

流程（单文件）：HELLO → HELLO_ACK（常规 ACK 包，seq=0）→ DATA[0..N] ↔ ACK[0..N] → FIN → FIN_ACK。窗口大小 512 块（2 MB），动态 RTT 超时（初始 3 秒，收敛至 max(4×RTT, 500ms)），最多 15 次重试。服务器仅对有序包发送 ACK；客户端在超时时从上次 ACK 处开始重传（Go-Back-N）。

块大小为 4096 字节——在吞吐量和 IP 分片丢包放大之间取得平衡。4096 字节 UDP 载荷加 14 字节头部（总计 4110 字节）分片为约 3 个 IP 片段，丢包率放大约 3 倍。在 5% 网络丢包率下，有效丢包率约 14%，Go-Back-N 配合 NAK 快速重传可有效处理。原始 32 KB 块分片为约 23 个片段（丢包放大约 23 倍）。

UDP 文件夹传输：客户端对每个文件分别发送 HELLO→DATA→FIN→FIN_ACK 会话（每个文件一次），使用 `transferType=0x01`，相对路径（前缀为源文件夹名，如 `"a\subdir\file.txt"`）作为 fileName。服务器独立处理每个文件，根据需要创建子目录。所有文件发送完毕后，客户端发送 `TypeFolderEnd` 包；服务器回复 ACK(seq=0) 并触发 `OnTransferComplete`。

FIN_ACK 具有双重用途：哈希匹配时服务器回复 `TypeFinAck`；FIN 后哈希失败时回复 `TypeFin`（带 1 字节正文）。客户端最多重试 FIN 5 次。

## 边界情况与异常处理

- `ObjectDisposedException` 和 `InvalidOperationException` 在所有传输循环和外部 try-catch 块中静默捕获——`Stop()`/`Cancel()` 关闭套接字期间 I/O 正在执行时会出现这些异常。
- UDP 内部循环中的 `SocketException` 视为超时（重传/重试），非致命错误。
- 绑定失败（地址不在任何网卡上）在 `TransferServer.Start()` 和 `TransferUdpServer.Start()` 中捕获——触发 `OnError` + `OnStopped`，UI 重新启用控件。
- 传输过程中语言切换被禁用。
- `TransferUdpServer.Stop()` 调用 `UdpClient.Close()`（非 `Stop()`）以中断 `ReceiveAsync()`。
- C# 5 不允许在 `catch` 块中使用 `await`（CS1985）。使用在 `catch` 中设置的标志变量，在 try-catch 之后检查，或对非关键的尽力而为发送使用即发即忘的同步 `Send()`。

### 异步/UI 线程安全

所有异步传输方法在每个 `await` 上使用 `.ConfigureAwait(false)`。否则 WinForms 的 `SynchronizationContext` 会导致每个异步续延在 UI 线程上恢复——使消息泵饥饿并在传输期间冻结 UI。服务器在 `Task.Factory.StartNew(LongRunning)` 上运行（无同步上下文），其 await 不会捕获 UI 线程，但为保持一致性仍应用 `ConfigureAwait(false)`。

### UDP 超时机制

使用 `Socket.ReceiveTimeout`——禁止使用 `Task.WhenAny(ReceiveAsync, Task.Delay)`。`Task.WhenAny` 模式每次迭代创建新的 `ReceiveAsync`；当延迟先完成时，前一个 `ReceiveAsync` 仍在等待，导致同一 `UdpClient` 上存在多个并发接收操作。这会破坏内部套接字状态并阻止重传数据被接收。`Socket.ReceiveTimeout` 与 `BeginReceive`/`EndReceive`（`UdpClient.ReceiveAsync` 的底层实现）正确集成，不会留下孤儿操作。

### UDP RTT 测量与动态超时

客户端发送每个窗口后记录时间。当首个推进 `sendBase` 的 ACK 到达时，计算 `rtt = now - sendTime`。通过 EWMA 维护平滑 RTT：`smoothRtt = smoothRtt × 0.875 + rtt × 0.125`。接收超时为 `max(smoothRtt × 4, MinTimeoutMs)`，其中 `MinTimeoutMs = 500`。初始 smoothRtt 默认为 750 ms（TimeoutMs / 4）。在局域网（RTT ~1ms）中超时收敛至 ~500ms；在广域网（RTT ~50ms）中约 200ms；在高延迟链路（RTT ~300ms）中约 1200ms。

### UDP 协议恢复机制

- **客户端 FIN 阶段 ACK 处理**：FIN 重试期间，客户端也监听数据 ACK。如果 ACK 指示 `seq + 1 < totalChunks`（服务器未收齐所有数据），客户端设置 `sendBase = seq + 1` 并通过统一 `while(true)` 循环回到数据阶段，无需代码重复。
- **服务器超时补发 ACK**：服务器接收超时时，重新发送最后一个累计 ACK（`expectedSeq - 1`）。帮助客户端推进窗口，无需等待其自身超时 + Go-Back-N 周期，更快从 ACK 丢失中恢复。
- **服务器 retryCount 重置**：收到任何有效 `TypeData` 包（有序或乱序）时 `retryCount` 重置为 0，而不仅是有序数据。防止服务器在客户端仍活跃重传时放弃。
- **服务器 FIN 数据未收齐处理**：如果 FIN 到达但 `expectedSeq < totalChunks`，服务器发送 `ACK(expectedSeq - 1)` 而非忽略 FIN。告知客户端需要重传哪些块。
- **NAK 选择性重传**：NAK 将特定块号入队。客户端从优先队列中发送它们，优先于新块。基于超时的重传仍使用 Go-Back-N（因为不知道具体缺失哪些块）。
- **批量 ACK**：服务器累积最多 512 块后发送一个累计 ACK（或 50ms 定时器触发）。相比每块 ACK 减少约 512 倍。
- **批量发送**：客户端将窗口中所有块发送作为 `Task.WhenAll` 启动，用 1 次 await 替代 512 次顺序 `await SendAsync` 调用。
- **I/O 缓冲**：客户端以 1 MB 缓冲读取文件（切片为多个块）。服务器写入 2 MB 缓冲，每窗口刷新一次。文件数据在 FIN_ACK 前刷新到磁盘以确保安全。
- **套接字缓冲区大小**：客户端和服务器均使用 4 MB 发送/接收缓冲区（`_udp.Client.SendBufferSize` / `ReceiveBufferSize = 4 * 1024 * 1024`）。

## 监控模式

客户端 UI 在文件路径字段下方有一个"监控模式"复选框。启用后：

- **`StartMonitoring(folderPath, ip, port)`**：创建"已发送文件"子目录，禁用 UI 输入控件，在目标文件夹上启动 `FileSystemWatcher`，在线程池线程上启动 `MonitorLoop`。
- **`MonitorLoop`**：处理循环——每次处理一个文件出队，调用 `ProcessMonitoredFile`，队列为空时睡眠 500ms。通过 `CancellationToken` 退出。
- **`ProcessMonitoredFile`**：(a) 调用 `WaitForFileReady` 确保文件写入完成；(b) 创建新的 `TransferClient` 或 `TransferUdpClient`，绑定最小事件集（日志、进度、`TaskCompletionSource<bool>` 用于完成跟踪）；(c) 成功后移入"已发送文件/"；(d) 失败时记录错误并保留文件。
- **`WaitForFileReady`**：每 500ms 轮询 `FileInfo.Length`。连续 2 次大小不变时返回 `true`。每 30 秒记录一条日志。2 分钟未稳定则返回 `false`——文件移至队列末尾（防止卡住的文件阻塞其他文件）。
- **事件绑定分离**：监控模式使用自己的事件处理器更新日志和进度条，但不调用 `ResetClientUI`。`TaskCompletionSource<bool>` 跟踪每个文件的成功/失败——`OnTransferComplete` 设为 true，`OnStopped` 设为 false（仅首次调用生效）。
- **停止**：监控中点击取消按钮调用 `StopMonitoring()`，取消令牌、释放 FileSystemWatcher、重置 UI。

## 运行时要求

- Windows 7 SP1 或更高版本
- .NET Framework 4.5 或更高版本（Win7 推荐 4.6.1+）
