# 文件互传与远程文件管理

基于 C#、.NET 10 和 WPF 的 Windows 局域网工具，提供文件传输和远程文件管理两套客户端 / 服务端程序。

## 功能

- **文件传输**：共享文件或文件夹，多连接并发下载，支持暂停、断点续传和 SHA-256 完整性校验。
- **远程文件管理**：浏览共享目录、上传和删除文件、查看图片、编辑文本，以及管理 EXE 程序的运行状态。
- **复制粘贴**：右键“复制”或 `Ctrl+C` 复制远程文件/文件夹，支持 `Ctrl+A` 全选和多选；在资源管理器中按 `Ctrl+V` 即可保存到本地。
- **图片查看**：保留当前画面并后台预取相邻图片，切换不再黑屏，重复查看直接命中内存缓存。
- **大目录**：列表按 500 条分页，滚动到底部自动加载；切换排序时自动取回全部条目。
- **可取消操作**：打包下载、压缩和上传过程中可以点击“取消”中止。
- **断线重试**：列表、文本和图片请求遇到网络中断会自动重试一次，并给出更明确的连接错误提示。
- **压缩下载**：在服务端创建 ZIP，或将文件夹打包下载。
- **日志**：使用 Serilog 记录运行信息，方便排查问题。

## 环境要求

- Windows 10 / 11
- 运行**完整版**发布包：无需安装 .NET 运行时
- 运行**精简版**发布包：需要安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)
- 从源码构建：需要 .NET 10 SDK，或安装支持 .NET 10 的 Visual Studio 及“.NET 桌面开发”工作负载

## 下载发布包

每个版本提供两个压缩包，按需选择：

| 发布包 | 体积 | 说明 |
| --- | --- | --- |
| `FileTransfer-<版本>-win-x64.zip` | 约 65 MB | 完整版。四个程序共用一份 .NET 运行时，解压即用，无需安装 .NET。 |
| `FileTransfer-<版本>-win-x64-framework.zip` | 约 1 MB | 精简版。框架依赖，四个程序分别放在独立目录，运行前需安装 .NET 10 桌面运行时。 |

完整版解压后，四个程序位于同一个目录：

```text
FileTransfer/
├─ RemoteFiles.Client.exe   远程文件管理客户端
├─ RemoteFiles.Server.exe   远程文件管理服务端
├─ Transfer.Client.exe      文件传输客户端
├─ Transfer.Server.exe      文件传输服务端
└─ README.md
```

这些程序共用同目录下的运行时文件，请**不要单独移动某个 exe**，否则可能无法启动。精简版则可以把单个程序目录整体拷走使用。

## 快速开始

从源码运行时，先在项目目录打开 PowerShell 并构建解决方案：

```powershell
dotnet build .\FileTransfer.sln -c Release
```

使用发布包时无需构建，解压后直接双击对应的 `.exe`：先启动服务端并设置共享目录和端口，再用客户端连接服务端 IP 和端口。

### 文件传输

分别启动服务端和客户端：

```powershell
.\Start-Server.ps1
# 在另一个终端运行
.\Start-Client.ps1
```

1. 在服务端添加共享文件或文件夹，点击“启动服务”（默认 TCP 端口 **9500**）。
2. 在客户端填写服务端 IP 和端口，连接并选择下载目录。
3. 选择文件或文件夹开始下载；暂停后可在相同目录继续下载。

### 远程文件管理

```powershell
.\Start-RemoteServer.ps1
# 在另一个终端运行
.\Start-RemoteClient.ps1
```

在服务端选择共享根目录并启动服务（默认 TCP 端口 **9501**），然后在客户端填写服务端 IP 和端口连接。

同一台电脑测试可使用 `127.0.0.1`；跨电脑连接请使用服务端局域网 IP，并按需放行对应防火墙端口。

## 可选：NG 过滤（RCMDB）

远程文件管理客户端支持按机台和日期只显示 NG 截图文件夹。该功能默认不生效，需要服务端配置 Oracle 连接：

1. 复制 `Local/NgFilter/ngfilter.example.json` 为 `%LOCALAPPDATA%\FileTransferDemo\ngfilter.json`，填入实际密码。
2. 重启服务端，在客户端勾选“NG 过滤”。
3. 只对 `A6` 开头的机台目录和日期目录生效；未配置时状态栏会提示“服务端未启用目录过滤”。

真实 `ngfilter.json` 已在 `.gitignore` 中排除，请勿提交。需要带该功能的自包含发布包时，运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Local\publish-ng.ps1
```

## 自检

```powershell
dotnet run --project .\Transfer.SmokeTests -c Release
dotnet run --project .\Transfer.WindowTests -c Release
```

## 注意事项

- 程序面向可信局域网，协议**没有身份认证和 TLS**。远程文件管理客户端可在共享根目录内执行读写及 EXE 管理操作，请谨慎选择共享目录和网络环境。
- 断点续传需要保留下载目录中的 `.part` 和 `.part.json` 文件；ZIP 下载和远程文件管理上传不支持断点续传。
- 日志和程序配置默认保存在 `%LOCALAPPDATA%\FileTransferDemo`。
