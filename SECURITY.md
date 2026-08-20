# 安全政策 / Security policy

## 支持范围

仅最新正式版本接收安全修复。旧版本用户应先升级到 [最新 Release](https://github.com/backinfile/CommandTray/releases/latest)。

## 报告漏洞

请优先通过 GitHub 的[私密漏洞报告](https://github.com/backinfile/CommandTray/security/advisories/new)提交问题，不要在公开 Issue 中披露漏洞细节、利用代码、密钥或个人数据。如果私密报告入口不可用，请只创建一条不含技术细节的 Issue，请求维护者建立私密联系方式。

报告中建议包含：

- 受影响版本和 Windows 版本；
- 问题影响与复现步骤；
- 最小化验证材料；
- 已知的缓解方法。

命令执行、MCP 本地访问边界、敏感环境变量或日志泄露、进程管理以及安装包完整性问题均属于安全报告范围。维护者会尽快确认、复现和评估报告，并在修复发布后协调披露。

善意研究、避免访问无关数据且不影响其他用户的报告者，不会因符合本政策的测试受到项目方追究。

---

## Supported versions

Security fixes are provided for the latest stable release only. Users of older versions should first upgrade to the [latest Release](https://github.com/backinfile/CommandTray/releases/latest).

## Reporting a vulnerability

Please use GitHub's [private vulnerability reporting](https://github.com/backinfile/CommandTray/security/advisories/new) whenever available. Do not disclose vulnerability details, exploit code, secrets, or personal data in a public issue. If private reporting is unavailable, open only a minimal issue asking the maintainer to establish a private contact channel, without technical details.

A useful report includes:

- the affected CommandTray and Windows versions;
- impact and reproduction steps;
- minimal validation material;
- any known mitigation.

Command execution, the MCP local-access boundary, sensitive environment variable or log disclosure, process management, and installer integrity are all in scope. The maintainer will acknowledge, reproduce, and assess reports as soon as practical, then coordinate disclosure after a fix is released.

Good-faith researchers who avoid unrelated data and do not disrupt other users will not face action from the project for testing performed in accordance with this policy.
