---
name: CommandTray Android
description: 跨设备工作空间与传输工具
colors:
  canvas: "#F5F6F0"
  surface: "#FFFFFF"
  primary: "#315545"
  panel: "#243E34"
  signal: "#D9F0A1"
  note: "#F7EBDD"
  text: "#1D2C24"
  muted: "#55645B"
  outline: "#D5DDD1"
  dark-canvas: "#151C18"
  dark-surface: "#202923"
---

## Overview

手机端优先展示当前空间、文件或剪贴板内容。配对、设备和 MCP 集中在设置。深松绿承载当前空间及同步文本，暖色用于区分便签。

## Layout

手机底部保留四个常驻入口。宽屏把同一组入口放在左侧，正文限制阅读宽度。每页先呈现标题和主要操作，下载、编辑、删除放在对应内容旁。标题、正文和标签遵循系统字体缩放。

页面标题、配对状态和空间新建入口共用一行顶部栏。页面标题为 20 sp，空间名称为 25 sp，正文为 15 sp。主题跟随 Android 的亮色和深色设置。

## Components

- 空间面板内常驻切换、拉取和同步。便签、待办和画板按原始顺序阅读，待办显示实际完成数。
- 共享文件按目录分组，单行保留下载入口，删除收在更多菜单。上传和保存沿用 Android 系统文件选择器。
- 剪贴板显示同步文本，发送本机和复制到本机分开操作。
- 配对状态用文字显示。设置中的 MCP 开关、地址和复制按钮保持在同一组。端点用等宽字体，密钥通过复制操作传递。
- 控件使用 48 dp 点击目标。按压状态使用系统 Ripple，内容通过色块与细分隔线区分。

参考了 [Android 布局与导航](https://developer.android.com/design/ui/mobile/guides/layout-and-content/layout-and-nav-patterns)、[Google Keep 的便签与清单](https://play.google.com/store/apps/details?id=com.google.android.keep)、[LocalSend 的文件传输界面](https://play.google.com/store/apps/details?id=org.localsend.localsend_app) 和 [KDE Connect 的设备配对与共享](https://play.google.com/store/apps/details?id=org.kde.kdeconnect_tp)。
