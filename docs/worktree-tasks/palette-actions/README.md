# 界面配色和操作布局

## 用户输入

> 搜搜全网教程，skill等，修改下配色，需要针对所有页签重新看看怎么重新布局所有操作，是否直接作为界面上的按钮，还是收入菜单

> 提交推送发布新版本，更新部署和我本机

## 计划与结果

- 参考 Fluent 2、Windows 命令栏、Apple 工具栏和 Carbon 行操作规范。Windows 和 Web 使用统一配色，逐页调整常用操作、对象菜单和删除确认。完整安排见 [界面设计](../../../DESIGN.md)。
- 用 Avalonia Headless 验证 Windows 工作区与工具页，用 Playwright 验证隔离中转上的四个 Web 页面、键盘焦点与窄屏菜单。Windows 与中转构建成功。
- Suite 发布使用 `release/suite/1.0.5` 和 `suite-v1.0.5`。构建由 GitHub Actions 执行。部署使用官方发布产物并校验 SHA-256，Windows 使用现有安装目录和数据。
