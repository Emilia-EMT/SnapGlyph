# Contributing to SnapGlyph

感谢你帮助改进 SnapGlyph。提交 Issue 或 Pull Request 前，请先确认改动没有破坏离线运行、CPU 推理和隐私边界。

## 开发环境

- Windows 10/11 x64
- .NET 10 SDK，版本要求见 `global.json`
- PowerShell 7

首次构建：

```powershell
pwsh .\scripts\fetch-models.ps1
dotnet restore .\src\SnapGlyph.slnx
dotnet build .\src\SnapGlyph.slnx -c Release
```

## 提交前检查

- 构建无错误；不要提交 `bin/`、`obj/`、`outputs/` 或 `work/`。
- 不要提交真实号码、客户截图、个人信息或未经脱敏的 OCR 样张。
- 不要提交 ONNX 模型二进制；更新模型时同时更新下载脚本和 `models/MANIFEST.sha256`。
- OCR 改动必须保持几何去重、单行输出和不按固定长度过滤。
- 扫码改动不得把 OCR 结果作为条码解码兜底。
- UI 改动应在 100%、125% 和 150% 缩放下检查窗口裁切。
- 性能相关改动请注明 CPU、模型、图片尺寸、选区大小和耗时。

## Pull Request 建议

PR 描述中请写明：

1. 用户可见的变化。
2. 为什么现有实现不足。
3. 是否改变 OCR/扫码规则或性能预算。
4. 已执行的构建和手工回归。
5. 可公开的脱敏复现材料。

## OCR / 条码回归重点

- 一张包含多行重复框的号码样张，只能输出一条。
- 同一区域重复识别，结果应稳定。
- 较短号码不能被固定长度规则丢弃。
- 严重倾斜、暗底浅字、反光和局部模糊样张不得让单次识别无限重试。
- Code 128、Code 39、ITF、EAN/UPC、QR、Data Matrix 等常见格式应至少覆盖一个样张。

## 代码风格

- 使用现有 C# / XAML 风格和 nullable 设置。
- 优先做局部、可验证的改动。
- 新依赖必须明确许可证、体积和 CPU 性能影响。
- 不在日志、通知、异常信息或测试文件中写入用户真实数据。

提交贡献即表示你同意按仓库的 MIT License 提供代码。
