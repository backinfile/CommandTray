# Code signing policy / 代码签名政策

## 当前状态

CommandTray 正在准备申请 SignPath Foundation 的免费代码签名服务。当前 GitHub Releases 中的安装包与便携版**尚未进行 Authenticode 签名**，因此 Windows 可能显示 Microsoft Defender SmartScreen 提示。

申请获批并完成接入后，项目将按 SignPath 要求注明：

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

在签名正式启用前，本页不会把任何发布包标记为已签名。

## 签名范围

签名仅用于 [backinfile/CommandTray](https://github.com/backinfile/CommandTray) 的官方 Windows 发布产物，包括：

- CommandTray 主程序；
- 便携版压缩包内由本项目生成的可执行文件；
- Windows 安装程序。

项目不会使用证书为其他项目、第三方二进制文件或用户配置的命令签名。

## 构建与签名流程

1. 在 `dev` 完成开发、审查和测试。
2. 从 `dev` 创建不可修改的 `release/X.Y.Z` 分支，并在同一提交创建 `vX.Y.Z` Tag。
3. GitHub Actions 从公开源代码执行可信构建和测试。
4. SignPath 获批后，由授权审批者手动批准签名请求；先签署应用程序，再使用已签名程序制作并签署安装包。
5. 对最终签名产物生成 SHA-256 校验值，并由 GitHub Actions 发布 Release。

正式 Release 分支和 Tag 不会重写。发现问题后，将回到 `dev` 修复、测试并发布新版本。

## 角色与审批

本项目目前由个人维护：

- 作者、提交者与审查者：[`@backinfile`](https://github.com/backinfile)
- 签名审批者：[`@backinfile`](https://github.com/backinfile)

外部贡献必须经过审查并合并到 `dev` 后才能进入发布构建。未经审查的外部代码不会直接获得签名审批。获得签名权限前，相关 GitHub 与 SignPath 账户必须启用双重身份验证。

## 安全事件

如果证书、构建流程或发布产物疑似受到破坏，维护者将停止分发受影响版本、调查影响范围，并在 `dev` 修复和测试后发布新版本；必要时会请求吊销相关证书或签名。

隐私与数据处理见 [PRIVACY.md](PRIVACY.md)，漏洞报告方式见 [SECURITY.md](SECURITY.md)。

---

## Current status

CommandTray is preparing an application for free code signing through the SignPath Foundation. The installer and portable packages currently published on GitHub Releases are **not yet Authenticode-signed**, so Microsoft Defender SmartScreen may display a warning.

If the application is accepted and signing is integrated, releases will carry the following acknowledgement:

> Free code signing provided by SignPath.io, certificate by SignPath Foundation.

No artifact will be described as signed on this page until signing is actually enabled.

## Signing scope

Signing is limited to official Windows artifacts produced by [backinfile/CommandTray](https://github.com/backinfile/CommandTray):

- the CommandTray application;
- project-built executables inside portable archives;
- the Windows installer.

The certificate will not be used for other projects, third-party binaries, or commands configured by users.

## Build and signing process

1. Development, review, and testing are completed on `dev`.
2. An immutable `release/X.Y.Z` branch and a `vX.Y.Z` tag are created from the same commit.
3. GitHub Actions performs a trusted build and test run from the public source.
4. After SignPath approval, an authorized approver manually approves each signing request. The application is signed first; the installer is then built from the signed application and signed separately.
5. SHA-256 checksums are generated from the final signed artifacts, which GitHub Actions publishes as a Release.

Published release branches and tags are never rewritten. Problems are fixed and tested on `dev`, then shipped as a new version.

## Roles and approval

This project is currently maintained by one person:

- Author, committer, and reviewer: [`@backinfile`](https://github.com/backinfile)
- Signing approver: [`@backinfile`](https://github.com/backinfile)

External contributions must be reviewed and merged into `dev` before they can enter a release build. Unreviewed external code is never approved directly for signing. GitHub and SignPath accounts with signing access must have two-factor authentication enabled before access is granted.

## Security incidents

If a certificate, build process, or release artifact is suspected to be compromised, the maintainer will stop distributing the affected version, investigate the impact, fix and test the issue on `dev`, and publish a new version. Certificate or signature revocation will be requested when appropriate.

See [PRIVACY.md](PRIVACY.md) for data handling and [SECURITY.md](SECURITY.md) for vulnerability reporting.
