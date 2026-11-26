---
name: CommandTray
description: Shared desktop and mobile workspace
colors:
  chrome: "#141D29"
  shell-accent: "#8DB8FF"
  dark-canvas: "#0F151E"
  dark-panel: "#182230"
  light-canvas: "#EAF0F8"
  light-panel: "#F8FAFE"
  terminal: "#111A27"
---

# CommandTray 界面设计

## 工作区

桌面侧栏默认宽度为 244。空间列表直接打开对应画布。工具按「设备与共享」「开发与运维」「屏幕与桌面」「实用工具」展开，只显示当前客户端支持的分组和条目。设置固定在底部。「工具总览」按同样的分组列出入口，并提供画板、待办、便签、本地目录和终端等可用组件的添加入口。顶部显示当前页面或空间名。「定位组件」按当前空间的组件名称和类型查找，选中后滚动到原卡片并高亮。拖动标题移动组件，拖动右下角调整大小，操作期间会显示点阵。按住 Shift 时，位置和尺寸以 24 像素为步长吸附。

用 `Ctrl+K` 搜索页面、空间、组件和当前可用的添加操作。方向键选择，Enter 跳转，Esc 关闭并回到先前的焦点。跳到组件时高亮边框。目录组件支持点路径返回上一级，并能在当前目录添加终端组件。

侧栏用顶部按钮或 `Ctrl+B` 在图标栏与带文字的侧栏之间切换。拖动右缘调整宽度，也可聚焦右缘后用方向键微调。宽度和折叠状态保存在当前设备，可通过本机 MCP 的 `settings_read` 和 `settings_save` 读写 `sidebarWidth`、`sidebarCollapsed`。设置入口固定在侧栏底部。

桌面组件标题栏中的最大化按钮把该组件铺满当前画布。点击还原或按 Esc 回到原有位置、尺寸与滚动位置。进行中的终端会话和输入框草稿留在同一组件内。

## 视觉和移动端

原生客户端和 Web 使用石墨蓝工作台。亮蓝强调主要操作、焦点和选中项。绿色标记运行、连接或完成，琥珀色表示等待处理，玫瑰红表示失败和需要确认的移除操作。状态同时显示文字。终端使用独立的输出区和等宽字体。[Fluent 2 色彩规范](https://fluent2.microsoft.design/color) · [WCAG 2.2](https://www.w3.org/TR/WCAG22/)

窄屏底栏提供「空间」「工具」「设置」。「空间」打开可搜索列表，「工具」打开工具总览。便签、待办和画板在同一条滚动流中。共享文件保留文件名、目录和行内下载操作，剪贴板历史显示文本、来源设备和发送时间。

## 操作布局

页面主按钮负责当前最常见的任务。选中对象的操作放在对象标题、详情或列表行内。搜索、筛选、取消和重试保持可见。低频操作使用带对象名称的「⋯」菜单，删除、断开和替换配置保留明确确认。窄屏上操作栏换行，表单和菜单支持键盘操作。[Windows 命令栏](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/command-bar) · [Apple 工具栏指南](https://developer.apple.com/design/human-interface-guidelines/toolbars) · [Carbon 行级操作](https://www.carbondesignsystem.com/building-blocks/core/components/data-table/guidelines)

| 页面或组件 | 直接显示 | 菜单或下一层 |
| --- | --- | --- |
| 空间 | 添加组件、定位、同步或配对 | 重命名空间、从中转拉取、删除空间 |
| 便签与待办 | 编辑便签、勾选、编辑待办、新增待办 | 复制便签、重命名和移除组件、删除待办 |
| 画板 | 抓手、绘图工具、撤销、重做、缩放、适应内容、导出 PNG。选中后显示复制、删除和层级操作 | 重置视图、切换网格、清空画板、原生端的笔色和线宽 |
| 目录与终端 | 选择目录、打开或预览、复制到指定位置、打开或关闭终端、运行时中断、发送 | 输入目录路径、新建目录、在此目录添加终端 |
| 命令管理 | 新建、当前命令启停和编辑、日志搜索和复制 | 刷新列表、重启、打开服务网页、删除 |
| 共享文件 | 上传、搜索和筛选、行内下载、传输取消和重试 | 刷新列表、复制文件名或 SHA-256、删除文件 |
| 剪贴板 | 发送文本、原生同步设置、搜索和筛选、当前记录复制和置顶 | 编辑后发送、处理文本、生成二维码、刷新历史、发送本机剪贴板、在本机隐藏 |
| 实用工具 | 文本转换、文本对比、时间戳、二维码、文件校验 | 从共享文件进入校验时预填 SHA-256 |
| 截图与录屏 | 范围、延时、截图、开始或结束录屏、历史搜索、复制图片、标注、播放和另存 | 上传到共享文件、放弃录屏、打开原图、贴图、识别文字和二维码、重命名、复制路径、定位文件和回收 |
| SSH 主机 | 添加主机、连接、编辑、密钥入口 | 删除主机，在密钥面板内导入或删除密钥 |
| Windows 桌宠 | 显示、隐藏。单击打招呼，双击打开工作区，拖动后停在原地 | 自动走动、睡觉或叫醒、调整大小 |
| 设置 | 配对、编辑连接、刷新设备、同步与应用开关、复制 MCP 配置、更新状态对应的操作、导出配置 | 解除配对、导入服务配置 |

## 参考

- 抓手、空格或中键拖动只平移视图。画板获得焦点后，可用方向键平移、Ctrl+滚轮以指针为中心缩放、Shift+1 适应内容。绘制时按住 Shift 可锁定比例或将线段吸附到 45°。清空可撤销，PNG 包含全部已保存的对象。[FigJam 平移与缩放](https://help.figma.com/hc/en-us/articles/1500004414582-Pan-and-zoom-in-FigJam) · [Miro 输入设备操作](https://help.miro.com/hc/en-us/articles/360017731053-Using-Miro-with-a-mouse-trackpad-or-touchscreen) · [Excalidraw 绘图指南](https://plus.excalidraw.com/how-to-start)

- 侧栏可调整宽度、可切成图标栏，显示与收起都有直接入口。[VS Code Custom Layout](https://code.visualstudio.com/docs/configure/custom-layout) · [Fluent NavigationView](https://learn.microsoft.com/en-us/windows/apps/design/controls/navigationview) · [IntelliJ IDEA Tool Windows](https://www.jetbrains.com/help/idea/tool-windows.html)
- Windows 桌宠「栗尾」有待机、走动、睡觉和挥手四个原创姿态，使用像素透明窗口。它在当前显示器的工作区内活动，悬停时停下，拖放后可通过菜单恢复自动走动。通知气泡可打开来源组件。[eSheep](https://github.com/Adrianotiger/desktopPet/blob/master/Readme.md) · [Desktop Pet Studio](https://github.com/PhantatP/desktop-pet-studio)
- 一个组件临时占满内容区，还原后保留原布局。分隔线支持键盘微调。[Windows Terminal Pane Zoom](https://learn.microsoft.com/en-us/windows/terminal/panes#zooming-a-pane) · [W3C Window Splitter](https://www.w3.org/WAI/ARIA/apg/patterns/windowsplitter/)

- 命令入口优先支持键盘搜索、选中和执行。拖动时才通过 Shift 启用吸附。[PowerToys Command Palette](https://learn.microsoft.com/en-us/windows/powertoys/command-palette/overview) · [FancyZones](https://learn.microsoft.com/en-us/windows/powertoys/fancyzones)
- 目录与终端都能作为同一级工作区组件，同屏切换相邻任务。[Wave Terminal](https://github.com/wavetermdev/waveterm) · [Zed 终端文档](https://zed.dev/docs/terminal) · [Files 双栏文档](https://www.files.community/docs/features/dual-pane)
- 空间内的内容具有各自的坐标和尺寸，用点阵辅助辨认位置。[Obsidian JSON Canvas](https://github.com/obsidianmd/jsoncanvas)
- 键盘焦点可见，窗口变窄时将侧栏移至底部。[W3C Focus Visible](https://www.w3.org/WAI/WCAG22/Understanding/focus-visible.html) · [Android 自适应导航](https://developer.android.com/develop/ui/compose/layouts/adaptive/build-adaptive-navigation)

样式和布局分别定义于 [应用样式](src/CommandTray.Ui/ClientApplication.axaml) 和 [共享页面](src/CommandTray.Ui/ClientView.axaml)。
