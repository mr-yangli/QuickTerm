# QuickTerm

**开发中**：VT100/XTerm 终端仿真内核 + ConPTY，目标形态是跨平台的终端 + SSH/SFTP 客户端（对标 Termora）。

从原 QuickNet 单仓拆分出的独立项目。**本族不上 NuGet**，源码随仓库。MIT 协议。

## 结构

- `src/QuickTerm.Terminal`：VT/xterm 解析与屏幕缓冲内核（零 NuGet 依赖）
- `src/QuickTerm.Pty`：ConPTY（Windows）/ Unix PTY 抽象（内核零 NuGet 依赖）
- `src/QuickTerm.App`：Avalonia 自绘宿主（`IsPackable=false`，永不发包；SSH/SFTP 传输亦在此层）

## 现状

- 本地终端（ConPTY）、拖选/复制/粘贴、回滚、resize reflow 已落地
- SSH 传输（Tmds.Ssh）已打通；SFTP 双栏文件传输（浏览 / 上传 / 下载 / 远程编辑）已落地
- 终端内核零 NuGet 依赖；闸门读数与缺口见 `docs/`

## 构建与测试

```bash
dotnet build QuickTerm.slnx
dotnet test tests/QuickTerm.Tests/QuickTerm.Tests.csproj
```

## 文档

见 `docs/`（`quickterm-p0-plan.md`、`quickterm-notes.md`）。

## License

MIT
