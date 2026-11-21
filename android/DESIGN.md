---
name: CommandTray Android
description: 跨设备工作空间与传输工具
colors:
  canvas: "#FAFAF7"
  surface: "#FFFFFF"
  primary: "#315545"
  text: "#1D2C24"
  muted: "#55645B"
  outline: "#DDE3DA"
  dark-canvas: "#151C18"
  dark-surface: "#202923"
---

## Overview

手机端打开即读当前空间、文件或剪贴板内容。用文字和细分隔线组织内容，绿色标记可操作项和当前入口。

## Layout

每页使用一个 48 dp 工具栏，字号为 18 sp。窄屏底部将图标和名称并排，宽屏用左侧导航栏，字号放大时图标和名称上下排列。字号与明暗主题跟随 Android。

## Components

- 工具栏直接显示可切换的空间名称，提供添加和同步入口。手动拉取、重命名放在空间菜单。便签、待办和画板按原始顺序展示。
- 共享文件按目录分组。下载靠近文件名，删除在文件菜单。上传和保存使用 Android 系统文件选择器。
- 剪贴板正文可选择，刷新、发送、复制位于顶部。长内容可打开全文。
- 配对状态随中转服务器设置显示。本地 MCP 的开关、地址和复制按钮就近排列。
- 可交互控件保持至少 48 dp 的点击目标。

参考了 [Android 布局与导航](https://developer.android.com/design/ui/mobile/guides/layout-and-content/layout-and-nav-patterns)、[Google Keep 的便签与清单](https://play.google.com/store/apps/details?id=com.google.android.keep)、[LocalSend 的文件传输界面](https://play.google.com/store/apps/details?id=org.localsend.localsend_app) 和 [KDE Connect 的设备配对与共享](https://play.google.com/store/apps/details?id=org.kde.kdeconnect_tp)。
