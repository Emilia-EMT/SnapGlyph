# Third-Party Notices

SnapGlyph 的项目代码采用 MIT License。正式 Release 还会包含或调用以下第三方组件与模型；它们继续遵循各自的许可证。

## NuGet dependencies

| Component | Version | License | Upstream |
| --- | --- | --- | --- |
| RapidOcrNet | 4.2.0 | Apache-2.0 | https://github.com/BobLd/RapidOcrNet |
| ZXingCpp | 0.5.3 | Apache-2.0 | https://github.com/zxing-cpp/zxing-cpp |
| OpenCvSharp4 | 4.13.0.20260627 | Apache-2.0 | https://github.com/shimat/opencvsharp |
| Microsoft.ML.OnnxRuntime | 1.29.0 | MIT | https://github.com/microsoft/onnxruntime |
| SkiaSharp | 3.119.1 | MIT | https://github.com/mono/SkiaSharp |
| Clipper2 | 2.0.0 | BSL-1.0 | https://github.com/AngusJohnson/Clipper2 |

NuGet restore 还会解析若干 .NET 平台与运行时包。构建产物中随包附带的许可证和第三方声明文件继续有效；若本清单与具体包的 `LICENSE` / `ThirdPartyNotices` 冲突，以对应包内的原始文件为准。

## Models bundled in official releases

| Model | Purpose | License | Source |
| --- | --- | --- | --- |
| PP-OCRv6 det/rec small | 标准 OCR | Apache-2.0 | https://github.com/RapidAI/RapidOCR |
| PP-OCRv6 det/rec medium | 高精度 OCR | Apache-2.0 | https://github.com/RapidAI/RapidOCR |
| PP-LCNet textline orientation classifier | 单行文字倒置判断 | Apache-2.0 | https://github.com/PaddlePaddle/PaddleOCR |
| PP-LCNet_x1_0_doc_ori | 文档方向判断 | Apache-2.0 | https://huggingface.co/PaddlePaddle/PP-LCNet_x1_0_doc_ori_onnx |
| DocAligner LCNet050 | 文档四角检测 | Apache-2.0 | https://github.com/DocsaidLab/DocAligner |

模型来源 URL 和 SHA-256 见 `models/MANIFEST.sha256` 与 `scripts/fetch-models.ps1`。

## Runtime and platform

- .NET 10 runtime components are distributed under their respective Microsoft licenses.
- OpenCV native binaries are distributed under the Apache-2.0 license.
- ONNX Runtime binaries include additional third-party notices supplied by Microsoft; see the package's `ThirdPartyNotices.txt`.

## Redistribution

如果重新发布 SnapGlyph 的便携包，请保留本文件、`LICENSE`、NuGet 包自带的许可证/声明文件，以及模型来源和校验信息。不要删除第三方版权文字，也不要用 SnapGlyph 的 MIT 许可证覆盖第三方组件原本的许可证。
