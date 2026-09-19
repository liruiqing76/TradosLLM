# 给 .sdlplugin（OPC 包）添加自签数字签名。
# 背景：Studio 2019 ValidatingPluginLocator.ValidateSignatures 对"已签名但签名均不匹配
# OpenX 证书"的包会走完 foreach 后 return true（反编译实证），因此自签即可让
# LoadPlugins 把插件归入 ValidatedDescriptors，不再每次启动弹"未认证插件 Yes/No"。
# 证书本机自动生成（CN=TradosToolkit Internal），不进仓库、无密钥分发问题。
param(
    [Parameter(Mandatory = $true)][string]$PackagePath
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase

if (-not (Test-Path $PackagePath)) { throw "package not found: $PackagePath" }

$cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq 'CN=TradosToolkit Internal' } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Subject 'CN=TradosToolkit Internal' `
        -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
    Write-Host "created self-signed cert $($cert.Thumbprint)"
}

$pk = [System.IO.Packaging.Package]::Open($PackagePath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite)
try {
    $mgr = New-Object System.IO.Packaging.PackageDigitalSignatureManager($pk)
    if ($mgr.IsSigned) {
        Write-Host "already signed, refreshing"
        $mgr.RemoveAllSignatures()
    }
    [System.Collections.Generic.List[System.Uri]]$parts = New-Object 'System.Collections.Generic.List[System.Uri]'
    foreach ($p in $pk.GetParts()) {
        if ($p.Uri.ToString() -notmatch '_xmlsignatures|_rels') { $parts.Add($p.Uri) }
    }
    $mgr.Sign($parts, [Security.Cryptography.X509Certificates.X509Certificate]$cert)
    Write-Host "signed $PackagePath ($($parts.Count) parts, thumbprint $($cert.Thumbprint))"
}
finally { $pk.Dispose() }
