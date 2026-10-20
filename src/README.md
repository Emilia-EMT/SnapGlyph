# SnapGlyph 源码

完整介绍、下载、快捷键、构建和第三方许可证说明请阅读仓库根目录的
[README](../README.md)。

本目录包含：

- `SnapGlyph.App`：WPF 桌面应用。
- `SnapGlyph.Core`：OCR、条码扫描和结果筛选。
- `SnapGlyph.Cli`：离线命令行验证工具。
- `CaptureSmoke`、`TransformSmoke`：截图和图像变换 smoke test。

模型文件不在 Git 仓库中。首次构建前请在仓库根目录运行：

```powershell
pwsh .\scripts\fetch-models.ps1
```
