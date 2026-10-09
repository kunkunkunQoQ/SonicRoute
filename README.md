# SonicRoute 历史源码与可运行版本备份

此分支于 2026-10-09 建立，保存本地各版本备份中的必要源码、资源、可运行文件和安装包，并补充主分支已有的独立旧源码及商店包。原始备份保持各版本目录结构，源码可直接在 GitHub 浏览。

- [按版本查看](INDEX.md)：`backups/` 保存历次本地版本，`supplementary-source/` 保存仅在旧源码归档中的补充版本。
- `store-packages/` 保存额外的微软商店安装包；各版本目录内的商店包也保留，包名、内容和原有签名状态不变。
- `backup-manifest.json` 记录每个原始文件的大小、SHA256、版本和存储方式。
- 本分支用于历史查阅和还原；当前正式版见 [v1.21 Release](https://github.com/kunkunkunQoQ/SonicRoute/releases/tag/v1.21)。历史应用沿用各自运行环境要求。

超过 40 MiB 的文件按原始字节分卷保存到 `large-files/`，原目录放置 `.restore.json` 指引；重复的大文件共享同一组分卷。还原会校验所有分卷及完整文件的 SHA256。其余文件直接保存，没有修改源码或程序内容。

下载本分支后，在分支根目录使用 PowerShell 7.2 或更新版本：

```powershell
# 核对全部备份和分卷，先不还原大文件
./Restore-Backups.ps1 -VerifyOnly
# 还原指定版本的大文件；其它文件原本就在版本目录中
./Restore-Backups.ps1 -Version 'v1.15'
# 只核对一个版本
./Restore-Backups.ps1 -Version 'v1.15' -VerifyOnly
```

不需要大文件还原的较新 Lite/Legacy 版本可从各自运行目录使用。签名私钥/证书材料、用户配置、私有规范、构建缓存和诊断日志不属于公开备份范围；这些内容没有上传。旧版 README 与内部记录不复制，以本索引和源码为查阅入口。

此分支独立于主分支，不改变应用行为；包含历史二进制会增加仓库克隆体积。只需此备份时可单独克隆分支：

```text
git clone --single-branch --branch backup/history https://github.com/kunkunkunQoQ/SonicRoute.git SonicRoute-History
```
