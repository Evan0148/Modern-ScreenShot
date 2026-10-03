# Agent.md — 项目协作约定

给 AI 代理与贡献者的项目级约定。当前内容：版本号规则。改规则前先改这里，CI 与文档跟随本文件。

## 版本号规则

采用 **SemVer 2.0** 双通道：正式版由人打 tag 决定，开发版由 CI 对每次推送自动编号。

| 通道 | 格式 | 示例 | 产生方式 |
|---|---|---|---|
| 正式版 | `MAJOR.MINOR.PATCH` | `0.1.0` | 手动推 `v*` 标签（如 `v0.1.0`），CI `release` job 自动构建并发布 |
| 开发版 | `<base>-dev.<N>+g<shortsha>` | `0.0.0-dev.12+gc795cdc` | 每次推送（非 tag），CI `dev-draft` job 自动编号并挂 Draft release |

### 各段含义

- **base**：最近的可达 `v*` 标签去掉 `v`（`git describe --tags --match 'v*'` 推导）；还没有任何 tag 时为 `0.0.0`。打了 `v0.1.0` 后，后续开发版自动变为 `0.1.0-dev.N+…`。
- **N**：GitHub Actions 的全局 run number（`GITHUB_RUN_NUMBER`），每次推送单调递增；同一 commit 重跑 CI 时 N 不变 → **同一 commit 永远得到同一版本号**。
- **+g\<shortsha\>**：构建元数据（commit 短 SHA），不参与 SemVer 优先级比较，只用于定位源码。
- **MAJOR.MINOR.PATCH** 语义：不兼容的破坏性变更 / 新功能 / 缺陷修复。1.0.0 之前（0.x）行为与配置格式可自由变化。

### 排序与一致性

- 同一 base 下 `dev.N` 随推送递增；`0.1.0-dev.99` < `0.1.0`（prerelease 恒低于同名正式版），因此打正式 tag 后版本号自然"超过"之前所有开发版。
- 一个 commit 只有一个版本号（N 与 SHA 都由 commit 决定）；Draft release 的 tag 固定为 `dev-<shortsha>`（与 commit 绑定），版本号写在标题与 zip 文件名里。

### 版本号的落点

1. **Releases 页**：Draft 标题 `Dev <版本号>`、zip 文件名 `ModernScreenShot-<版本号>-win-x64.zip`（文件名里 `+` 写作 `-`）。
2. **二进制**：CI 发布时传 `-p:InformationalVersion=<版本号>`；应用「设置 → 关于」与启动日志通过 `AppVersion.Display` 读取并显示。本地构建由 .NET SDK 自动生成 `1.0.0+<完整SHA>`，同样走该路径显示。

### 发正式版流程

1. 选定要发布的 commit（通常是需要发版的 main 最新提交）。
2. `git tag v0.1.0 && git push origin v0.1.0` —— CI `release` job 自动构建 self-contained zip 并创建正式 Release（自动 changelog）。
3. 不需要处理历史 Draft；它们只是测试构建，可随时手动清理。

### 给代理的提醒

- 新建 Release / Draft 时必须遵守上述命名；不要手工发明别的版本格式。
- 提到版本号时用完整 SemVer（含 `-dev.N` 与 `+g<sha>`），不要只写 `1.0`。
- 修改版本推导逻辑时，同步更新 `.github/workflows/ci.yml`、README 的「构建与 CI」节与本文件。
