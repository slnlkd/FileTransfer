# 文件互传与远程文件管理

基于 C#、.NET 10 和 WPF 的 Windows 局域网工具，提供文件传输和远程文件管理两套客户端 / 服务端程序。

## 功能

- **文件传输**：共享文件或文件夹，多连接并发下载，支持暂停、断点续传和 SHA-256 完整性校验。
- **远程文件管理**：浏览共享目录、上传和删除文件、查看图片、编辑文本，以及管理 EXE 程序的运行状态。
- **压缩下载**：在服务端创建 ZIP，或将文件夹打包下载。
- **日志**：使用 Serilog 记录运行信息，方便排查问题。

## 环境要求

- Windows 10 / 11
- .NET 10 SDK（从源码构建）
- 或使用支持 .NET 10 的 Visual Studio，并安装“.NET 桌面开发”工作负载

## 快速开始

在项目目录打开 PowerShell，先构建解决方案：

```powershell
dotnet build .\FileTransfer.sln -c Release
```

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

## 自检

```powershell
dotnet run --project .\Transfer.SmokeTests -c Release
dotnet run --project .\Transfer.WindowTests -c Release
```

## 注意事项

- 程序面向可信局域网，协议**没有身份认证和 TLS**。远程文件管理客户端可在共享根目录内执行读写及 EXE 管理操作，请谨慎选择共享目录和网络环境。
- 断点续传需要保留下载目录中的 `.part` 和 `.part.json` 文件；ZIP 下载和远程文件管理上传不支持断点续传。
- 日志和程序配置默认保存在 `%LOCALAPPDATA%\FileTransferDemo`。
