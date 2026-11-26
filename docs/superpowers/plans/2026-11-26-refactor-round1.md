# 第一轮执行计划

基线：f974252；分支：codex/refactor-annotation。保留 v1.6.15，不修改协议、数据库、布局及标注后关闭行为，不发布或推送。

## 任务

- [x] 保存目录操作与路径栏加载提示的稳定基线。
- [ ] 按导航、标注/NG、日志拆分测试；等待真实布局完成后验证下一批可见性；补充超时诊断；连续三轮验证。
- [ ] 增加 IAnnotationClient 及远程适配器；增加无 WPF 控件依赖的 AnnotationViewModel。
- [ ] 提取 AnnotationPanel，保留样式、弹层、历史、字段对齐；宿主保留图片展示和窗口协调。
- [ ] 运行双端构建、业务状态测试、窗口回归；审查并分步提交。

## 接口约定

- IAnnotationClient：读取/新增字典、读取历史、提交、删除当前图片所有标注，沿用现有 DTO，所有方法接收 CancellationToken。
- AnnotationViewModel：表单与当前图片/历史、字典、校验错误、IsDirty/IsWriting/IsAddingDictionary；加载、新增、提交和撤销操作；统一处理取消与过期结果。
- AnnotationPanel：构造注入状态模型；选择当前图片；成功事件提供 Submit 或 Revoke 结果；只处理呈现、弹层、焦点和提示。
- ImageViewer：接收成功事件，更新 AnnotationSubmitted，关闭窗口并通知现有宿主刷新；不再直接请求标注或持有表单控件。

## 验收

新状态测试覆盖追加去重、校验、失败保留输入、并发提交拦截、切图/关闭后旧结果不回写、字典添加期间切图、取消、提交/撤销事件。
现有窗口检查覆盖已标注筛选、字段样式、历史、弹层、字典、提交/撤销后关闭、主页激活及下一批定位，保留覆盖。
