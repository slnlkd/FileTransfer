# NG 过滤部署说明

NG 过滤已纳入版本控制，发布包同样包含该功能；但只有在服务端配置 `ngfilter.json` 后才生效。

客户端项目直接引用 NG 界面源码；服务端项目直接引用 NG 查询、标注仓储及 Oracle 驱动。全新检出后运行 `dotnet restore FileTransfer.sln` 和 `dotnet build FileTransfer.sln -c Release` 即可构建，`Directory.Build.local.props` 仅用于可选本机覆盖。

## 部署步骤

1. 使用发布包中的 `FileTransfer.Server` 目录部署服务端，确保包含 `Oracle.ManagedDataAccess.dll`。
2. 将配置文件放到服务端部署根目录，与 `FileTransfer.Server.exe` 放在一起。

   ```text
   FileTransfer.Server\ngfilter.json
   ```

   参考仓库中的 `Local/NgFilter/ngfilter.example.json`，填写实际数据库密码。真实配置包含密码，请单独部署，不要提交到 git，也不要随发布包分发。
3. 客户端使用同一版本的 `FileTransfer.Client`，工具栏会显示“NG 过滤”勾选框。
4. 重启服务端并点击“启动服务”，客户端连接后可勾选“NG 过滤”或打开机台目录查看 NG 总览。

不需要该功能时不配置 `ngfilter.json` 即可；勾选后状态栏会提示“服务端未启用目录过滤”。

## 本地打包

需要生成带 NG 的自包含包时运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Local\publish-ng.ps1
```

脚本会编译客户端和服务端、校验 Oracle 驱动与 NG 控件、附带示例配置，并输出压缩包路径。

## 排查

- 出现“服务端未配置 NG 总览查询”或“服务端未启用目录过滤”时，在部署目录下的 `Logs\RemoteFiles.Server\` 中搜索 `只看NG配置`，核对配置路径和加载状态。
- 提示“目录过滤失败，已显示全部”：检查服务器到 Oracle 的网络和数据库账号。
- 勾选后仍显示全部且没有任何提示：检查客户端和服务端是否使用同一版本。
