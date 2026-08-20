# Repository instructions

## 分支与发布

- `dev` 是唯一主干和默认分支；`main` 禁止继续提交、合并或发布。
- 小改动可直接提交到 `dev`；大功能从 `dev` 创建功能分支，CI 通过后合并并删除。
- 发布前在 `dev` 完成测试，并按需更新面向用户的 `README`。
- 从 `dev` 创建 `release/X.Y.Z`，在同一提交打 `vX.Y.Z` Tag，由项目 GitHub Actions workflow 发布。
- release 分支和正式 Tag 不可修改；发现问题须回到 `dev` 修复、测试，再发布新版本。修复问题时要添加简要注释，记录问题原因和修复内容。

## Worktree

- worktree 创建目录：`../worktrees/projectName/xxxx`。
