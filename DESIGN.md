---
name: CommandTray
description: Shared desktop and mobile workspace
colors:
  chrome: "#101D32"
  shell-accent: "#7BE8F7"
  dark-canvas: "#0A1426"
  dark-panel: "#14233A"
  light-canvas: "#E8EFF9"
  light-panel: "#F9FCFF"
  terminal: "#080F1E"
---

# CommandTray 界面设计

## 工作区

桌面在点阵画布上并排显示目录和终端。窄工具栏集中放置空间页签、快速查找、同步和添加组件。拖动标题移动组件，拖动右下角调整大小。按住 Shift 时，位置和尺寸以 24 像素为步长吸附。

用 `Ctrl+K` 搜索页面、空间、组件和当前可用的添加操作。方向键选择，Enter 跳转，Esc 关闭并回到先前的焦点。跳到组件时高亮边框。目录组件支持点路径返回上一级，并能在当前目录添加终端组件。

侧栏用顶部按钮或 `Ctrl+B` 在图标栏与带文字的侧栏之间切换。拖动右缘调整宽度，也可聚焦右缘后用方向键微调。宽度和折叠状态保存在当前设备，可通过本机 MCP 的 `settings_read` 和 `settings_save` 读写 `sidebarWidth`、`sidebarCollapsed`。空间页签多时横向滚动，设置入口固定在侧栏底部。

桌面组件标题栏中的最大化按钮把该组件铺满当前画布。点击还原或按 Esc 回到原有位置、尺寸与滚动位置。进行中的终端会话和输入框草稿留在同一组件内。

## 视觉和移动端

深蓝色用于工具栏和导航，青色标记当前空间、焦点与快捷入口。不同组件用图标和颜色区分。内容表单、列表和面板跟随系统明暗主题，终端使用独立的深色输出区和等宽字体。

手机顶部保留一行空间切换、同步和添加入口，底部放四个常用页面。便签、待办和画板在同一条滚动流中。共享文件保留文件名、目录和行内下载操作，剪贴板首屏显示正文。

## 参考

- 侧栏可调整宽度、可切成图标栏，显示与收起都有直接入口。[VS Code Custom Layout](https://code.visualstudio.com/docs/configure/custom-layout) · [Fluent NavigationView](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview) · [IntelliJ IDEA Tool Windows](https://www.jetbrains.com/help/idea/tool-windows.html)
- 一个组件临时占满内容区，还原后保留原布局。分隔线支持键盘微调。[Windows Terminal Pane Zoom](https://learn.microsoft.com/en-us/windows/terminal/panes#zooming-a-pane) · [W3C Window Splitter](https://www.w3.org/WAI/ARIA/apg/patterns/windowsplitter/)

- 命令入口优先支持键盘搜索、选中和执行。拖动时才通过 Shift 启用吸附。[PowerToys Command Palette](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview) · [FancyZones](https://learn.microsoft.com/en-us/windows/powertoys/fancyzones)
- 目录与终端都能作为同一级工作区组件，同屏切换相邻任务。[Wave Terminal](https://github.com/wavetermdev/waveterm) · [Zed 终端文档](https://zed.dev/docs/terminal) · [Files 双栏文档](https://www.files.community/docs/features/dual-pane)
- 空间内的内容具有各自的坐标和尺寸，用点阵辅助辨认位置。[Obsidian JSON Canvas](https://github.com/obsidianmd/jsoncanvas)
- 键盘焦点可见，窗口变窄时将侧栏移至底部。[W3C Focus Visible](https://www.w3.org/WAI/WCAG22/Understanding/focus-visible.html) · [Android 自适应导航](https://developer.android.com/develop/ui/compose/layouts/adaptive/build-adaptive-navigation)

样式和布局分别定义于 [应用样式](src/CommandTray.Ui/ClientApplication.axaml) 和 [共享页面](src/CommandTray.Ui/ClientView.axaml)。
