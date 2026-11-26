# 第一轮执行计划

基线：f974252；分支：codex/refactor-annotation。保留 v1.6.15，不修改协议、数据库、布局及标注后关闭行为，不发布或推送。

## 任务

- [x] 保存目录操作与路径栏加载提示的稳定基线。
- [x] 按导航、图片、标注/NG、日志拆分测试；等待真实布局完成后验证下一批可见性；补充超时诊断；连续三轮验证。
- [x] 增加 IAnnotationClient 及远程适配器；增加无 WPF 控件依赖的 AnnotationViewModel。
- [x] 提取 AnnotationPanel，保留样式、弹层、历史、字段对齐；宿主保留图片展示和窗口协调。
- [x] 运行双端构建、业务状态测试、窗口回归；审查并分步提交。

## 接口约定

- IAnnotationClient：读取/新增字典、读取历史、提交、删除当前图片所有标注，沿用现有 DTO，所有方法接收 CancellationToken。
- AnnotationViewModel：表单与当前图片/历史、字典、校验错误、IsDirty/IsWriting/IsAddingDictionary；加载、新增、提交和撤销操作；统一处理取消与过期结果。
- AnnotationPanel：构造注入状态模型；选择当前图片；成功事件提供 Submit 或 Revoke 结果；只处理呈现、弹层、焦点和提示。
- ImageViewer：接收成功事件，更新 AnnotationSubmitted，关闭窗口并通知现有宿主刷新；不再直接请求标注或持有表单控件。

## 验收

新状态测试覆盖追加去重、校验、失败保留输入、并发提交拦截、切图/关闭后旧结果不回写、字典添加期间切图、取消、提交/撤销事件。
现有窗口检查覆盖已标注筛选、字段样式、历史、弹层、字典、提交/撤销后关闭、主页激活及下一批定位，保留覆盖。

## 实施结果

- f974252 保存操作优化基线；c04cbd9 分组回归测试；33472e4 提取标注接口和状态模型；最后一步提取表单、独立操作人设置，并补全双端与竞态验证。
- ImageViewer 从 2380 行降至 1722 行；右侧布局和弹层在 AnnotationPanel，表单与异步状态在 AnnotationViewModel，远程请求通过 IAnnotationClient 适配。
- 窗口测试按导航、日志、图片、标注/NG 分文件；增加 --suite state/navigation/logs/images/annotation/interaction 分组入口。完整入口仍包含所有检查。
- 基线完整窗口回归连续通过三轮；最终 Release 构建零警告、零错误；153 项冒烟通过；状态、图片、双端标注专项及完整窗口回归通过。

## 审查与取舍

- 独立只读代码审查发现的写入期间切图、失败新增丢弃首次字典两项问题，均先补失败测试再修复；覆盖提交和撤销、两种响应顺序及晚到结果。
- 同时恢复服务端返回字典项的标准大小写、错误文字的悬浮说明；不遗留已知审查问题。
- 保留代码构建的 UserControl，以维持原有布局和主题资源；没有同时迁移 XAML。代价是表单呈现仍约 600 行，后续可按确切需要提取更小控件。
- 保留共享源码链接与现有公开查看器入口；不创建界面程序集。代价是共用组件须持续验证两种宿主。
- 工作位于独立工作区的 codex/refactor-annotation 分支，未合并、推送或发布；版本仍为 v1.6.15。

## 复核命令

- dotnet build FileTransfer.sln -c Release --no-restore -v quiet
- dotnet run --project Transfer.SmokeTests -c Release --no-build
- dotnet run --project Transfer.WindowTests -c Release --no-build
- dotnet run --project Transfer.WindowTests -c Release --no-build -- --suite state
- dotnet run --project Transfer.WindowTests -c Release --no-build -- --suite annotation
- dotnet run --project Transfer.WindowTests -c Release --no-build -- --suite images
