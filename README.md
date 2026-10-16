# GameSaveVault · 游戏存档管理器

游戏存档，再也不会丢。

一款为单机玩家打造的 **Windows 存档保险柜**：自动发现 Steam 与本地游戏的存档位置，游戏退出瞬间自动快照，历史版本一键还原，支持多玩家档案与 WebDAV 多机同步。**免安装、零后台推送、数据 100% 留在本机。**

[![GitHub release (含预发布)](https://img.shields.io/github/v/release/auroray2016/GameSaveVault?include_prereleases&label=beta)](https://github.com/auroray2016/GameSaveVault/releases)
[![平台](https://img.shields.io/badge/Windows-10%20%2F%2011%20x64-0078D4)](https://github.com/auroray2016/GameSaveVault/releases)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![下载量](https://img.shields.io/github/downloads/auroray2016/GameSaveVault/total?label=下载量)](https://github.com/auroray2016/GameSaveVault/releases)

![GameSaveVault 主界面](website/shots/main-1600.png)

---

## 它解决什么问题

换电脑、重装系统、游戏更新坏档、手滑覆盖存档、云存档抽风回档……几百小时的进度可能在几秒钟内消失。GameSaveVault 在后台替你盯住每一款游戏，**游戏一退出，快照已就位**。

## 功能特性

- **🔍 双通道自动扫描**：一键扫描 Steam 库清单与任意本地文件夹，自动认领游戏、匹配中文名与封面；也可按 AppId 从 Steam 商店精准认领
- **⏱️ 退出自动快照**：监控游戏进程，退出瞬间完成一次完整备份；长时间游玩可开启「运行中定时快照」；每款游戏保留份数可调，超量自动滚动淘汰
- **⏪ 历史版本一键还原**：每次备份都是完整快照，打开时间线选一个时间点即可回到过去
- **♻️ 最近删除（回收站）**：手动删除、删除玩家、移除游戏、自动淘汰、清理残留——五种方式删掉的备份都会进入「最近删除」，**保留 15 天**，随时可整份还原（文件归位、记录回库、云端对账自动恢复）
- **☁️ WebDAV 云同步**：填入坚果云等任意 WebDAV 网盘即可多台电脑互相同步，跨设备自动互认身份，云端多余备份可一键认领回本地
- **🀄 中文名与封面**：Steam 官方商店搜索为主、SteamTools 补充，自动抓取中文名和封面图
- **📁 多玩家档案**：一台电脑多人玩同一个游戏？建立多个玩家档案分别备份，互不干扰；另有「心目中的存档」指针快速定位每人最珍爱的进度
- **⭐ 收藏与排序**：收藏游戏置顶，支持按最近添加 / 最近游玩 / 名称排序
- **🌗 深色 / 浅色主题**：自绘沉浸式界面，壁纸通铺，深浅主题一键切换
- **📦 真·便携版**：单文件 exe、解压即用、自带 .NET 8 运行时，目标机零依赖；放 U 盘随身带

## 下载与使用

前往 **[Releases 页面](https://github.com/auroray2016/GameSaveVault/releases)** 下载最新 beta：

1. 下载 `GameSaveVault-x.x.x-beta-portable-x64.zip`
2. 解压到任意目录（含 U 盘）
3. 双击 `GameSaveVault.exe` 运行，无需安装、无需预装运行库
4. 首次使用：**扫描 Steam**（或扫描文件夹）→ 勾选游戏 → 之后全自动

> 系统要求：Windows 10 / 11 x64。每个 Release 页面都附有安装包 SHA256，下载后可自行校验。

## 数据与隐私

- 备份默认保存在 `%APPDATA%\GameSaveVault\backups`，回收站为其下的 `_recycle` 目录
- **不注册账号、不收集遥测、不向任何服务器上传数据**
- 只有你主动配置 WebDAV 后，存档才会加密传输到**你自己的**网盘账户
- 所有逻辑开源可查，完全离线可用

## 从源码构建

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（Windows）。

```powershell
# 调试运行
dotnet run --project src/GameSaveManager/GameSaveManager.csproj

# Release 构建
dotnet build src/GameSaveManager/GameSaveManager.csproj -c Release

# 全量冒烟测试（约 980 项：备份/还原/增量链/云对账/回收站等）
dotnet run --project tests/SmokeTest/SmokeTest.csproj -c Release
```

构建单文件便携版：

```powershell
dotnet publish src/GameSaveManager/GameSaveManager.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=false `
  -o publish/out
```

## 项目结构

```
src/GameSaveManager/   WPF (.NET 8) 主程序
  Models/              游戏、备份记录、回收站索引等数据模型
  Services/            备份、存储、云同步、玩家档案、回收站等核心服务
  Assets/              图标与界面素材
  Data/                离线中文名库（随包外置）
tests/SmokeTest/       端到端冒烟测试（真实文件系统 + 临时沙箱）
website/               产品主页与界面截图
```

## 技术栈

C# / .NET 8 · WPF + WinForms（部分系统对话框）· 单文件自包含发布，无第三方数据库与服务端依赖。

## 状态

当前为 **Beta**，功能持续打磨中。欢迎在 [Issues](https://github.com/auroray2016/GameSaveVault/issues) 反馈 bug、游戏识别问题与功能建议。
