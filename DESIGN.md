---
name: CommandTray
description: Shared desktop and mobile workspace
colors:
  canvas: "#EDF1EF"
  paper: "#FDFEFC"
  chrome: "#F6F8F5"
  text: "#172823"
  secondary: "#4E625B"
  line: "#D8E1DB"
  accent: "#28624F"
  selected: "#DCECE2"
  terminal: "#15231E"
---

# CommandTray 界面设计

## 内容和操作

空间、共享文件和剪贴板直接显示用户内容。顶栏只放当前空间或页面名称及当前操作。状态紧邻对应对象，例如命令的运行状态和设置内的配对状态。

## 桌面和手机

桌面使用窄侧栏和工作区切签。多个目录与终端在画布上并排显示，边框和组件标题用于定位与操作。命令页使用表格式布局。共享文件按目录分组，下载按钮和该文件同一行。

手机使用同一份页面和数据。底部为四个常用入口，工作区紧凑排列便签、待办、画板和 SSH 主机资料。空间切换和同步放在同一行。

## 颜色和字号

绿色用于主要动作及当前选择，浅色分隔线用于组织列表。终端采用独立深色底，选中文字可复制。应用明暗主题跟随系统。具备焦点、悬停与按压状态的控件同时使用文字或图标传递含义。

样式和布局分别定义于 [应用样式](src/CommandTray.Ui/ClientApplication.axaml) 和 [共享页面](src/CommandTray.Ui/ClientView.axaml)。
