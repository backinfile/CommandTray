# Repository instructions

## 分支与发布

| 发布线 | 维护分支 | 发布分支 | 发布标签 |
| --- | --- | --- | --- |
| 原版 Windows CommandTray | `dev` | `release/X.Y.Z` | `vX.Y.Z` |
| CommandTray Suite | `codex/suite-dev` | `release/suite/X.Y.Z` | `suite-vX.Y.Z` |

- `dev` 是 GitHub 仓库的默认分支。`main` 禁止继续提交、合并或发布。
- 功能分支从对应的维护分支创建，CI 通过后合回同一条发布线。套件功能分支使用 `codex/suite/*`。
- 两条发布线分别递增版本号、生成变更记录并发布 GitHub Release。原版的下载入口使用 GitHub 的 `Latest`，套件通过 `suite-v` 标签查找发布。
- 发布前验证对应维护分支上的版本。从该分支的提交建立发布分支，在同一提交创建对应标签。GitHub Actions 验证来源并为该发布线生成产物。
- 已发布的分支和标签保持不可变。发现问题后回到对应维护分支修复，验证后发布新版本。

## Worktree

- worktree 创建目录：`../worktrees/projectName/xxxx`。
