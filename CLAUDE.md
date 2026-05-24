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

十二个源文件编译为单个 WinForms exe：

- **Program.cs** — 入口。`[STAThread]` Main 启动 `MainForm`。
- **MainForm.cs** — GUI。服务器和客户端面板同时显示（已移除模式选择单选按钮）。**服务器协议**：`_chkServerTcp`/`_chkServerUdp` 复选框，可多选同时启动两个协议。**客户端协议**：`_rbClientTcp`/`_rbClientUdp` 单选按钮，独立于服务器。**并发控制**：`_numConcurrency` NumericUpDown（1-64），控制客户端多连接并行传输数量。服务器绑定地址下拉框（从活动网卡获取）、文件夹模式/监控模式复选框。动态进度卡片：服务器接收进度和客户端发送进度拆分为两个独立的 `FlowLayoutPanel`（左右排列），每个传输一个卡片（进度条+速度），服务端多客户端独立卡片，客户端每次传输独立卡片，完成 3 秒后自动移除。各自面板下方有独立状态标签。所有 UI 由代码构建（无设计器）。右上角语言下拉框。日志上限 500 条，进度事件节流约 100ms。事件绑定抽取到 `WireClientEvents`/`WireUdpClientEvents`/`WireConcurrentEvents` 辅助方法。服务器完成传输后 UI 保持"监听中..."状态（不重新启用所有输入控件）——服务器仍在运行并接受新传输。`ResetClientUI` 重置客户端控件和状态文本为"就绪"。`_serverCount` 计数器防止一个协议停止时误启用全部 UI。**监控模式**：`_chkMonitor` 复选框启用文件夹监控——启动时先扫描目录中已有文件并入队，然后 `FileSystemWatcher` 检测新文件，等待文件稳定（连续 2 次 500ms 轮询文件大小不变），通过 TCP 或 UDP 发送，成功后移入"已发送文件"子目录。2 分钟内未稳定的文件移到队列末尾。每个文件独立进度卡片。详见下方"监控模式"章节。
- **TransferServer.cs** — 异步 TCP 服务器。绑定到可配置地址+端口，并发接受多个连接（fire-and-forget）。`HandleClient` 读取 1 字节类型前缀，分发到 `HandleFileTransfer`（单文件 0x00）、`HandleFolderTransfer`（文件夹 0x01）或 `HandleChunkedFile`（分块文件 0x02）。文件夹传输带每个文件的相对路径和 SHA256。使用 `Task.Factory.StartNew(LongRunning)` 执行接受循环。设置 `NoDelay = true` 和 256KB 缓冲区。`OnClientConnected`/`OnClientProgress`/`OnClientTransferComplete` 事件按客户端 IPEndPoint 区分，支持并发多客户端独立进度跟踪。`_chunkTrackers`（`ConcurrentDictionary<string, ChunkTracker>`）聚合分块传输，`ChunkTracker` 延迟创建 `FileStream`（lock 内首次写入时），单 lock 完成写入+完成检查防止 TOCTOU 竞态。`Stop()` 时清理未完成的 ChunkTracker。
- **TransferClient.cs** — 异步 TCP 客户端。`SendAsync()` 发送单文件，`SendFolderAsync()` 发送文件夹，`SendChunkedAsync(offset, chunkSize, totalSize)` 发送分块文件（类型 0x02）。前两者委托到共享的 `RunTransfer` 包装器进行生命周期管理。发送文件元数据 + 数据 + SHA256 哈希。设置 `NoDelay = true` 和 256KB 缓冲区。`SendFilePayload` 辅助方法接受 `fileOffset` 参数，支持从文件中间开始读取（用于分块传输）。构造函数支持 `localPort` 参数绑定特定源端口。`RunTransfer` 中非取消异常重新抛出以正确传播错误到并发编排器。
- **TransferUdpSession.cs** — 每个 UDP 客户端的独立传输会话。包含完整接收状态（SHA256、FileStream、2MB 写缓冲、expectedSeq、NAK 状态）。通过 `EnqueuePacket`（返回 bool 表示是否成功入队）从 `ReceiveLoop` 接收包，内部通过 `SemaphoreSlim` 等待并处理。支持 FolderEnd。HELLO transferType 0x02（分块文件）解析 totalFileSize/chunkOffset/chunkSize。分块完成后从临时文件复制数据到 ChunkTracker。`IsRunning` 为 false 时拒绝新包（防止跨文件 HELLO 丢失）。所有 await 使用 `ConfigureAwait(false)`。`_chunkTrackers` 由 `TransferUdpServer` 传入（实例级，非 static）。
- **TransferUdpServer.cs** — 可靠 UDP 服务器。`ReceiveLoop` 接收所有包，按 `clientEp` 分发到 `ConcurrentDictionary<IPEndPoint, TransferUdpSession>`。HELLO 创建新 Session（`OnSessionStarted` 仅对新 session 触发，重传 HELLO 直接回复 ACK），DATA/FIN/FolderEnd 入队到对应 Session。`_chunkTrackers` 字典从服务端传入每个 Session。每个 Session 独立运行，拥有自己的进度、日志事件。4 MB 套接字缓冲区。
- **TransferUdpClient.cs** — 可靠 UDP 客户端。`SendAsync()` 发送单文件，`SendFolderAsync()` 发送文件夹，`SendChunkedAsync(offset, chunkSize, totalSize)` 发送分块文件（HELLO transferType 0x02）。使用滑动窗口（512 块 × 4096 字节 = 2 MB 窗口）、超时重传、丢包时 Go-Back-N。`SendUdpFileDataAsync()` 接受 `fileOffset` 参数支持分块读取。`CreateUdpClient()` 支持绑定特定源端口。`RunUdpTransfer` 中非取消异常重新抛出。所有异步方法使用 `ConfigureAwait(false)`。
- **ConcurrentTransfer.cs** — 多连接并发传输编排器。`SendAsync()` 将单文件切分为 N 个等大分块，并行发送（每个分块一个独立连接 + 独立源端口）。`SendFolderAsync()` 用 `SemaphoreSlim` 控制并发度，并行发送 N 个文件。聚合子传输进度（单文件：所有分块已传输字节之和；文件夹：已完成文件数及累计字节）。源端口通过 `Utils.FindFreePort(basePort + index + 1)` 分配。支持 TCP 和 UDP。
- **UdpProtocol.cs** — 共享的 UDP 线协议常量和辅助方法。此处定义包格式和类型。`BuildPacketFromBuffer` 从源缓冲区+偏移直接构建包，避免中间正文拷贝；`BuildPacket` 委托给它。`BuildPacketFromBuffer` 对 null 源（空包体 ACK/NAK/FIN_ACK）有防护。
- **Config.cs** — 键值配置持久化。保存/加载到 `%AppData%\TrFileTransfer\config.ini`。`Get`/`GetInt`/`GetBool`/`Set`/`SetInt`/`SetBool` 辅助方法。启动时加载，关闭时保存。
- **Shared.cs** — `TransferProgress` 结构体（值类型，免堆分配）、`FileEntry` 结构体、`ChunkTracker` 类（分块重组追踪：文件名聚合、延迟 FileStream 创建、`GetOrCreate`/`WriteChunk` 共享辅助方法）、`Utils` 静态辅助方法（`FormatSize`、`ConstantTimeEquals`、`LogTo`、`SanitizeRelativePath`、`GetUniqueSavePath`、`EmptyBytes`、`FindFreePort`）。所有传输文件通用，避免重复。
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

传输类使用 `volatile bool _isRunning` / `_disposed` 标志和 `CancellationTokenSource`。UI 事件处理器通过 `MainForm.cs` 中的 `this.Invoke()` 封送。`TransferUdpSession` 使用 `SemaphoreSlim` + `Queue<byte[]>` 作为包调度通道。触发事件前使用局部变量快照模式：`var handler = OnXxx; if (handler != null) handler(...);`。

## TCP 线协议

所有传输以 1 字节类型开始：`0x00` = 单文件，`0x01` = 文件夹，`0x02` = 分块文件（并发传输）。

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

### 分块文件（类型 0x02）

```
[1 byte:  0x02]
[8 bytes: Int64 完整文件总大小]
[8 bytes: Int64 此块在文件中的字节偏移]
[8 bytes: Int64 此块数据大小]
[4 bytes: Int32 文件名长度]
[N bytes: UTF-8 文件名]  // 所有块共享同一文件名，服务端以此聚合
[M bytes: 分块数据（chunkSize 字节）]
[32 bytes: 此块内容的 SHA256 哈希]
```

服务器按文件名聚合到 `ChunkTracker`，首次写入时预分配文件大小，后续块 seek到偏移写入。全部块收齐后触发 `OnTransferComplete`。每个块独立 SHA256 校验，单块损坏只丢弃该块（客户端超时重传）。

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

HELLO 正文 transferType：`0x00` = 单文件（服务器用 `Path.GetFileName` 清理文件名），`0x01` = 文件夹文件（服务器保留相对路径，创建子目录），`0x02` = 分块文件（正文：totalFileSize(8) + chunkOffset(8) + chunkSize(8) + nameLen(2) + fileName(N)）。每个分块走独立的 HELLO→DATA→FIN→FIN_ACK 周期。

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
