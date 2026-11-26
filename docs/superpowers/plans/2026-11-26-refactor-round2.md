# 第二轮：目录导航与 NG 进度状态

基线 9203ce6，同一隔离分支 codex/refactor-annotation；版本和 UI 不变，不发布、推送或合并。

- [x] 提取目录请求接口、导航状态、分页与未过滤目录缓存；保留 500 条分页、10 秒缓存、排序、返回定位和树联动。
- [x] 提取 NG 查询、日期校验、筛选计数、日期行与展开状态；保留原位更新和下一批选择。
- [x] 增加无窗口状态测试：迟到请求、断线重连、取消、分页去重、缓存失效、查询竞态、筛选和容器复用。
- [x] 验证双端构建、冒烟、完整窗口回归和独立审查，分模块提交。

目录状态只管理数据与请求生命周期；WPF 控件、忙碌提示的绘制、焦点、滚动和窗口联动仍由视图负责。NG 状态保留稳定的日期行及批次集合，界面只绑定和呈现。后续独立拆分查看窗口协调器、文件操作及主窗口入口，本轮不同时扩大改动范围。

## 验收记录

- DirectoryNavigationState / IDirectoryClient：目录首屏、分页、请求版本、已呈现列表版本、会话取消、目录树缓存与请求合并。RemoteEntryComparer 管理自然排序。
- NgProgressViewModel / NgDayRow：日期校验与快捷范围、筛选和总数、原位集合更新、展开记录、下一批选择及过期查询隔离。
- 独立审查发现的问题已通过失败复现后修复：等待新首屏期间不能加载旧分页；取消后原列表继续分页；取消唯一等待者后清理后台读取；失效树请求保留重试；旧连接握手不能覆盖新连接；旧排序/全选不能修改新列表。
- 分页仅处理新收到的条目标注状态，避免反复遍历累计列表。
- 保留完整窗口断言；剪贴板检查等待可读取图片，而不只检查 OLE 已公布格式。首次完整检查复现过格式已存在但 GetImage 尚未可读的时序。
- 最终双端 Release 构建零警告、零错误；状态专项、导航专项、完整窗口回归通过；153 项冒烟顺序重跑通过。
- 首次并行冒烟在未改动的 Downloader.SaveAsync 原子替换断点文件时出现 UnauthorizedAccessException；顺序重跑通过。本轮未改动下载器，保留该日志供后续文件操作重构复核。
- 未合并、推送、打包或修改版本。所有工作保留在当前隔离分支。

复核命令：dotnet build FileTransfer.sln -c Release --no-restore -v quiet；dotnet run --project Transfer.WindowTests -c Release --no-build；dotnet run --project Transfer.SmokeTests -c Release --no-build。状态和导航可通过 -- --suite state / navigation 单独运行。
