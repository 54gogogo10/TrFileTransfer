# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

本文件为 Claude Code 在此仓库中工作时提供指引。

## 构建

```
cd TrFileTransfer && build.bat
```

使用已安装 .NET Framework 4.x 自带的 `csc.exe` 生成 `TrFileTransfer.exe`。引用：`System.dll`、`System.Core.dll`、`System.Windows.Forms.dll`、`System.Drawing.dll`。无需 Visual Studio 或 MSBuild，只需 .NET Framework 运行时（包含 `csc.exe`）。

编译器为 C# 5（内置 `csc.exe` 支持的最高语言版本），生成的 exe 运行在 .NET Framework 4.5+（推荐 4.6.1）。避免 C# 6+ 语法：禁止字符串插值（`$`）、禁止表达式体成员、禁止空条件运算符（`?.`）、禁止异常过滤器（`when`）、禁止数字分隔符。

## 测试

```
# 构建测试：
cd TrFileTransfer\Tests && build.bat

# 运行测试：
TrFileTransfer.Tests.exe
```

测试套件包含单元测试（L10N、Config、Shared 工具方法）和集成测试（TCP 单文件、TCP 文件夹、UDT 单文件）。使用自定义轻量级 `TestRunner` + `Assert` 类，无外部测试框架依赖。

集成测试每次从 `TcpListener` 获取空闲端口并绑定本地回环（127.0.0.1），使用临时目录。超时：TCP 30 秒，UDT 60 秒。退出码 0 表示全部通过，1 表示有失败。

## 架构

八个源文件编译为单个 WinForms exe：

- **Program.cs** — 入口。`[STAThread]` Main 启动 `MainForm`。
- **MainForm.cs** — GUI。服务器和客户端面板同时显示（已移除模式选择单选按钮）。协议选择（TCP/UDT）、服务器绑定地址下拉框（从活动网卡获取）、文件夹模式/监控模式复选框。动态进度卡片：服务器接收进度和客户端发送进度拆分为两个独立的 `FlowLayoutPanel`（左右排列），每个传输一个卡片（进度条+速度），服务端多客户端独立卡片，客户端每次传输独立卡片，完成 3 秒后自动移除。各自面板下方有独立状态标签。所有 UI 由代码构建（无设计器）。右上角语言下拉框。日志上限 500 条，进度事件节流约 100ms。事件绑定抽取到 `WireClientEvents`/`WireUdtClientEvents` 辅助方法。服务器完成传输后 UI 保持"监听中..."状态（不重新启用所有输入控件）——服务器仍在运行并接受新传输。`ResetClientUI` 重置客户端控件和状态文本为"就绪"。**监控模式**：`_chkMonitor` 复选框启用文件夹监控——启动时先扫描目录中已有文件并入队，然后 `FileSystemWatcher` 检测新文件，等待文件稳定（连续 2 次 500ms 轮询文件大小不变），通过 TCP 或 UDT 发送，成功后移入"已发送文件"子目录。2 分钟内未稳定的文件移到队列末尾。每个文件独立进度卡片。详见下方"监控模式"章节。
- **TransferServer.cs** — 异步 TCP 服务器。绑定到可配置地址+端口，并发接受多个连接（fire-and-forget）。`HandleClient` 读取 1 字节类型前缀，分发到 `HandleFileTransfer`（单文件）或 `HandleFolderTransfer`（文件夹，带每个文件的相对路径和 SHA256）。使用 `Task.Factory.StartNew(LongRunning)` 执行接受循环。设置 `NoDelay = true` 和 256KB 缓冲区。新增 `OnClientConnected`/`OnClientProgress`/`OnClientTransferComplete` 事件（按客户端 IPEndPoint 区分），支持并发多客户端独立进度跟踪。
- **TransferClient.cs** — 异步 TCP 客户端。`SendAsync()` 发送单文件，`SendFolderAsync()` 发送文件夹。两者委托到共享的 `RunTransfer` 包装器进行生命周期管理。发送文件元数据 + 数据 + 每个文件的 SHA256 哈希。设置 `NoDelay = true` 和 256KB 缓冲区。`SendFilePayload` 辅助方法由单文件和文件夹传输共享。`SendFileInternal` 和 `SendFolderInternal` 均在成功时触发 `OnTransferComplete`。
- **TransferUdt.cs** — UDT STREAM 传输。`UdtNative` 提供 P/Invoke 声明（udt_socket/udt_bind/udt_listen/udt_accept/udt_connect/udt_send/udt_recv/udt_close 等），`UdtDll` 在首次使用时从嵌入资源提取 udt.dll 到 exe 目录。`TransferUdtServer` 复用 TCP 线协议（1 字节类型前缀 + 元数据 + 数据 + SHA256），`HandleClient` 添加/移除 client-specific progress handler，支持 `OnClientProgress`/`OnClientTransferComplete` 事件（按 IPEndPoint 区分）。`TransferUdtClient` 同样复用 TCP 线协议，`SendAsync()`/`SendFolderAsync()` 委托到 `RunUdtTransfer`。UDT STREAM 模式提供可靠有序字节流，无需应用层 ARQ/ACK/NAK。设置 30 秒收发超时。所有 I/O 通过 `UdtIo` 辅助方法（`UdtReadExactAsync`/`UdtWriteExactAsync`）封装 `udt_recv`/`udt_send` 的 `Task.Run` 包装。
- **Config.cs** — 键值配置持久化。保存/加载到 `%AppData%\TrFileTransfer\config.ini`。`Get`/`GetInt`/`GetBool`/`Set`/`SetInt`/`SetBool` 辅助方法。启动时加载，关闭时保存。
- **Shared.cs** — `TransferProgress` 类、`Utils` 静态辅助方法（`FormatSize`、`ConstantTimeEquals`、`LogTo`、`SanitizeRelativePath`、`GetUniqueSavePath`）。四个传输文件通用，避免重复。
- **L10N.cs** — 本地化字符串。静态 `L` 类根据 `L.IsChinese` 返回英文或中文文本。命名规则见下方"本地化约定"。
- **Tests/TestProgram.cs** — 测试入口。`TestProgram.Main` 先运行单元测试再运行集成测试，退出码反映结果。同时包含 `UnitTests` 类、`Assert` 辅助类（`Equal/True/False/NotNull/Throws`）、`TestRunner.Run(name, action)` 追踪通过/失败计数。
- **Tests/IntegrationTests.cs** — TCP/UDT 端到端集成测试。`FindFreePort()` 动态获取可用端口。

### 本地化约定

GUI 有语言下拉框（English / 中文），设置 `L.IsChinese`。所有本地化字符串在 `L10N.cs` 中定义。按消费者命名：
- `L.S_*` — TCP 服务器日志消息
- `L.C_*` — TCP 客户端日志消息
- `L.UdtS_*` — UDT 服务器日志消息
- `L.UdtC_*` — UDT 客户端日志消息
- 其他 `L.*` 属性 — MainForm UI 字符串

新增日志消息时遵循以上命名约定添加到 `L10N.cs`。禁止使用内联 `L.IsChinese ? "..." : "..."` 三元表达式。语言切换时 `PopulateBindAddresses()` 重新读取网卡并更新第一个下拉项标签。传输过程中语言切换被禁用（下拉框不可用）。

### 文件名冲突处理

TCP 和 UDT 服务器都使用 `Utils.GetUniqueSavePath`，在基础文件名（扩展名前）追加 `_1`、`_2` 等后缀，解决与保存目录中已有文件/目录的名称冲突。

### 线程安全模式

传输类使用 `volatile bool _isRunning` 标志和 `CancellationTokenSource`。UI 事件处理器通过 `MainForm.cs` 中的 `this.Invoke()` 封送。触发事件前使用局部变量快照模式：`var handler = OnXxx; if (handler != null) handler(...);`。

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

## UDT 传输

UDT 传输基于 libudt4 修复版，使用 STREAM 模式。UDT 在 UDP 之上提供可靠、有序的字节流（类 TCP 语义），由库内部处理拥塞控制、丢包恢复和重排序。应用层无需实现 ARQ、ACK/NAK、滑动窗口或 RTT 测量。

**应用层协议复用了相同的 TCP 线协议**（见上方"TCP 线协议"章节）。TransferUdtServer 和 TransferUdtClient 发送/接收与 TCP 对等类相同格式的数据，仅传输层不同。

### DLL 部署

`udt.dll` 通过 csc.exe 的 `/resource` 选项嵌入到 exe 中。首次运行时，`UdtDll.EnsureExtracted()` 检测 exe 目录下是否存在 `udt.dll`，若不存在则从嵌入资源提取。随后的运行直接使用已提取的 DLL。

### P/Invoke

`UdtNative` 静态类声明了核心 UDT API（CallingConvention.Cdecl）：
- 生命周期：`udt_startup()` / `udt_cleanup()`
- 套接字：`udt_socket()` / `udt_close()`
- 连接：`udt_bind()` / `udt_listen()` / `udt_accept()` / `udt_connect()`
- I/O：`udt_send()` / `udt_recv()`（通过 `Task.Run` 包装实现异步）
- 选项：`udt_setsockopt()` / `udt_getsockopt()`（用于设置 `UDT_RCVTIMEO` / `UDT_SNDTIMEO` 为 30 秒）
- 错误：`udt_getlasterror()` / `udt_getlasterror_desc()`

### 服务器架构

`TransferUdtServer.Start()`：确保 DLL 已提取 → `udt_startup()` → `udt_socket(AF_INET, SOCK_STREAM, 0)` → `udt_bind()` → `udt_listen(10)` → LongRunning accept 循环。

Accept 循环对每个客户端连接执行 fire-and-forget `HandleClient`，该函数添加/移除 client-specific progress handler（模式与 TCP TransferServer 相同），支持 `OnClientProgress`/`OnClientTransferComplete`（按 IPEndPoint 区分）。Stop() 调用 `udt_close()` 中断 accept 循环，然后 `udt_cleanup()`。

## 边界情况与异常处理

- `ObjectDisposedException` 和 `InvalidOperationException` 在所有传输循环和外部 try-catch 块中静默捕获——`Stop()`/`Cancel()` 关闭套接字期间 I/O 正在执行时会出现这些异常。
- 绑定失败（地址不在任何网卡上）在 `TransferServer.Start()` 和 `TransferUdtServer.Start()` 中捕获——触发 `OnError` + `OnStopped`，UI 重新启用控件。
- 传输过程中语言切换被禁用。
- `TransferUdtServer.Stop()` 调用 `udt_close()` 以中断阻塞中的 `udt_accept()`。
- C# 5 不允许在 `catch` 块中使用 `await`（CS1985）。使用在 `catch` 中设置的标志变量，在 try-catch 之后检查。

### 异步/UI 线程安全

所有异步传输方法在每个 `await` 上使用 `.ConfigureAwait(false)`。否则 WinForms 的 `SynchronizationContext` 会导致每个异步续延在 UI 线程上恢复——使消息泵饥饿并在传输期间冻结 UI。服务器在 `Task.Factory.StartNew(LongRunning)` 上运行（无同步上下文），其 await 不会捕获 UI 线程，但为保持一致性仍应用 `ConfigureAwait(false)`。

### UDT 超时与错误处理

UDT STREAM 模式通过 `udt_setsockopt` 设置 `UDT_RCVTIMEO` 和 `UDT_SNDTIMEO` 为 30 秒。超时后 `udt_recv`/`udt_send` 返回 -1 (ERROR)，调用方可检查 `udt_getlasterror()` 获取详细错误码。`udt_close()` 用于中断阻塞中的 I/O 操作（类似 TCP 的 socket close）。常见的 UDT 错误码：`ERR_CONNLOST`(7)、`ERR_NOSERVER`(2)、`ERR_CONNREJ`(3)。

## 监控模式

客户端 UI 在文件路径字段下方有一个"监控模式"复选框。启用后：

- **`StartMonitoring(folderPath, ip, port)`**：创建"已发送文件"子目录，禁用 UI 输入控件，在目标文件夹上启动 `FileSystemWatcher`，在线程池线程上启动 `MonitorLoop`。
- **`MonitorLoop`**：处理循环——每次处理一个文件出队，调用 `ProcessMonitoredFile`，队列为空时睡眠 500ms。通过 `CancellationToken` 退出。
- **`ProcessMonitoredFile`**：(a) 调用 `WaitForFileReady` 确保文件写入完成；(b) 创建新的 `TransferClient` 或 `TransferUdtClient`，绑定最小事件集（日志、进度、`TaskCompletionSource<bool>` 用于完成跟踪）；(c) 成功后移入"已发送文件/"；(d) 失败时记录错误并保留文件。
- **`WaitForFileReady`**：每 500ms 轮询 `FileInfo.Length`。连续 2 次大小不变时返回 `true`。每 30 秒记录一条日志。2 分钟未稳定则返回 `false`——文件移至队列末尾（防止卡住的文件阻塞其他文件）。
- **事件绑定分离**：监控模式使用自己的事件处理器更新日志和进度条，但不调用 `ResetClientUI`。`TaskCompletionSource<bool>` 跟踪每个文件的成功/失败——`OnTransferComplete` 设为 true，`OnStopped` 设为 false（仅首次调用生效）。
- **停止**：监控中点击取消按钮调用 `StopMonitoring()`，取消令牌、释放 FileSystemWatcher、重置 UI。

## 运行时要求

- Windows 7 SP1 或更高版本
- .NET Framework 4.5 或更高版本（Win7 推荐 4.6.1+）
