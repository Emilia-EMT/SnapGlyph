[CmdletBinding()]
param(
    [string]$Destination = (Join-Path (Split-Path -Parent $PSScriptRoot) "models"),
    [switch]$Force,
    [switch]$VerifyOnly
)

$ErrorActionPreference = "Stop"
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$curl = Get-Command "curl.exe" -ErrorAction SilentlyContinue
if (-not $VerifyOnly -and $null -eq $curl) {
    throw "curl.exe was not found. Windows 10/11 normally includes it."
}

$files = @(
    [pscustomobject]@{
        Path = "docaligner/lcnet050_p_multi_decoder_l3_d64_256_fp32.onnx"
        Sha256 = "32d186080ce16442674d4c0eaaaaac878eea289b56a8d1284f05fff1ff42e220"
        Url = "https://github.com/DocsaidLab/DocAligner/releases/download/1.1.1/lcnet050_p_multi_decoder_l3_d64_256_fp32.onnx"
    },
    [pscustomobject]@{
        Path = "docaligner/PP-LCNet_x1_0_doc_ori.onnx"
        Sha256 = "af9a0a4f317ff0709ce752067807f819cb15d883f8ecad89f28df1c6ee2d9c92"
        Url = "https://huggingface.co/PaddlePaddle/PP-LCNet_x1_0_doc_ori_onnx/resolve/main/inference.onnx"
    },
    [pscustomobject]@{
        Path = "v5/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"
        Sha256 = "54379ae5174d026780215fc748a7f31910dee36818e63d49e17dc598ecc82df7"
        Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv5/cls/ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx"
    },
    [pscustomobject]@{
        Path = "v6/PP-OCRv6_det_small.onnx"
        Sha256 = "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f"
        Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv6/det/PP-OCRv6_det_small.onnx"
    },
    [pscustomobject]@{
        Path = "v6/PP-OCRv6_rec_small.onnx"
        Sha256 = "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884"
        Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv6/rec/PP-OCRv6_rec_small.onnx"
    },
    [pscustomobject]@{
        Path = "v6/ppocrv6_dict.txt"
        Sha256 = "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d"
        Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv6/rec/PP-OCRv6_rec_small/ppocrv6_dict.txt"
    },
    [pscustomobject]@{
        Path = "v6/PP-OCRv6_det_medium.onnx"
        Sha256 = "92078b7355007ccfffcd4c8cd441a3afd4538904d06881b29a155e1e679907c2"
        Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv6/det/PP-OCRv6_det_medium.onnx"
    },
    [pscustomobject]@{
        Path = "v6/PP-OCRv6_rec_medium.onnx"
        Sha256 = "eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b"
        Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv6/rec/PP-OCRv6_rec_medium.onnx"
    },
    [pscustomobject]@{
        Path = "v6/ppocrv6_medium_dict.txt"
        Sha256 = "b5f2bfe2bdd9448429e3e82b51c789775d9b42f2403d082b00662eb77e401c5d"
        Url = "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/paddle/PP-OCRv6/rec/PP-OCRv6_rec_medium/ppocrv6_dict.txt"
    }
)

foreach ($file in $files) {
    $target = Join-Path $destinationPath ($file.Path -replace "/", [System.IO.Path]::DirectorySeparatorChar)
    $directory = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $directory -Force | Out-Null

    $currentHash = $null
    if (Test-Path -LiteralPath $target) {
        $currentHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    if ($currentHash -eq $file.Sha256) {
        Write-Host "OK       $($file.Path)"
        continue
    }

    if ($VerifyOnly) {
        throw "Missing or invalid model: $($file.Path)"
    }

    if ($currentHash -and -not $Force) {
        Write-Warning "Replacing invalid model: $($file.Path)"
    }

    $partial = "$target.part"
    if (Test-Path -LiteralPath $partial) {
        Remove-Item -LiteralPath $partial -Force
    }

    Write-Host "Download $($file.Path)"
    & $curl.Source --fail --location --silent --show-error --retry 3 --retry-delay 2 --output $partial $file.Url
    if ($LASTEXITCODE -ne 0) {
        if (Test-Path -LiteralPath $partial) {
            Remove-Item -LiteralPath $partial -Force
        }
        throw "Download failed: $($file.Url)"
    }

    $downloadedHash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($downloadedHash -ne $file.Sha256) {
        Remove-Item -LiteralPath $partial -Force
        throw "SHA-256 mismatch: $($file.Path)"
    }

    Move-Item -LiteralPath $partial -Destination $target -Force
    Write-Host "VERIFIED $($file.Path)"
}

Write-Host "All SnapGlyph models are present and verified."
