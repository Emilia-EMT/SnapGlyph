# SnapGlyph Models

运行 SnapGlyph 需要约 183 MB 的 ONNX 模型和字典文件。这些二进制文件不提交到 Git：

- 普通用户：直接下载官方 Release 中的便携包。
- 源码开发者：在仓库根目录运行 `pwsh .\scripts\fetch-models.ps1`。

目录结构：

```text
models/
  docaligner/
    lcnet050_p_multi_decoder_l3_d64_256_fp32.onnx
    PP-LCNet_x1_0_doc_ori.onnx
  v5/
    ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx
  v6/
    PP-OCRv6_det_small.onnx
    PP-OCRv6_rec_small.onnx
    ppocrv6_dict.txt
    PP-OCRv6_det_medium.onnx
    PP-OCRv6_rec_medium.onnx
    ppocrv6_medium_dict.txt
```

文件来源和许可证见根目录 `THIRD_PARTY_NOTICES.md`。SHA-256 校验值见 `MANIFEST.sha256`；下载脚本会在移动文件到目标路径前完成校验。
