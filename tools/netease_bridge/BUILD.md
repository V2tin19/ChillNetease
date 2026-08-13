# 构建 ChillNetease.dll（Go 原生桥接库）

`ChillNetease.dll` 是基于 [go-musicfox](https://github.com/go-musicfox/go-musicfox)（MIT）的二次封装：把网易云 API（登录 / 歌单 / 搜索 / 播放地址）以 C 导出（`-buildmode=c-shared`）暴露给 C# 插件 P/Invoke 调用。

> 插件不依赖此 DLL 的源码——直接使用编译产物即可。此文档仅供需要自行构建/修改桥接层时参考。

## 环境

- Go 1.22+（本机验证 1.26.2）
- Windows + CGO：MinGW-w64 gcc（x86_64），需满足 `go build` 的 CGO amd64 条件

## 步骤

```bash
# 1. 克隆 go-musicfox
git clone https://github.com/go-musicfox/go-musicfox.git gmfx

# 2. 把本目录所有 .go 文件复制到 gmfx 的 netease_bridge/ 子目录（与本仓库同源）
cp *.go ChillNetease.h ../gmfx/netease_bridge/

# 3. 补齐依赖（按 main.go 的 import 为准）
cd ../gmfx/netease_bridge
go mod tidy          # 或按需 go get，再 go mod vendor

# 4. 编译（产物：ChillNetease.dll + ChillNetease.h）
go build -buildmode=c-shared -o ChillNetease.dll .
```

## 导出接口（C# P/Invoke 侧）

- 初始化/状态：`NeteaseInit` / `NeteaseIsLoggedIn` / `NeteaseSetCookie` / `NeteaseLogout`
- 二维码登录：`NeteaseQRGetKey` / `NeteaseQRCheckStatus` / `NeteaseQRCancelLogin`
- 账号：`NeteaseGetUserInfo` / `NeteaseGetUserPlaylists` / `NeteaseGetPlaylistSongs`
- 播放：`NeteaseGetSongURL` / `NeteaseGetLikeSongs` / `NeteaseLikeSong`
- 搜索：`NeteaseSearchSongs`（搜索单曲，type=1，weapi）

详细签名见 `ChillNetease.h`（编译产物，见上方步骤 4）。
