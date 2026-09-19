# 消除 Trados Studio 2019 "未认证插件 Yes/No" 启动弹窗（自签 .sdlplugin 方案）

> 适用场景：内网/离线环境使用自研 Trados Studio 插件，无法上架 RWS AppStore 拿官方签名，
> 每次启动 Studio 都被弹 `Unsigned SDL Trados Studio Plug-in Found`，必须手点"是"。
> 本方案让 Studio 判定插件"已验证"，**彻底不再弹窗**，无需买证书、无需改 Studio 任何文件。
>
> 本文以 TradosToolkit 为例，机制适用于**任何** .sdlplugin 插件；文末有可直接复制的脚本与构建目标。
> 结论均经反编译 Studio 15.0.1.36320 本机 DLL + 用 Studio 自己的校验代码实测验证（2026-09-20）。

---

## 一、弹窗从哪来（反编译证据链）

### 1. 弹窗触发点：`Sdl.Desktop.Platform.dll` → `Studio.LoadPlugins()`

```csharp
ValidatingPluginLocator validatingPluginLocator = new ValidatingPluginLocator(defaultPluginLocator);
foreach (IPluginDescriptor invalidDescriptor in validatingPluginLocator.InvalidDescriptors)
{
    if (invalidDescriptor is IThirdPartyPluginDescriptor third)
    {
        if (third.InvalidSdlAssemblyReferences.Count > 0)
            SplashScreenShowInvalidSdlAssemblyDialog(...);          // 引用白名单问题 → 另一种对话框
        else if (SplashScreenAskYesNoQuestion(
            ThirdParty_CertificationWarning /* "Unsigned ... Plug-in Found" */, ...) == DialogResult.Yes)
        {
            validatingPluginLocator.ValidatedDescriptors.Add(invalidDescriptor);  // ← 只加进本次会话
        }
    }
}
```

关键点：
- 点"是"的结果**不写任何配置、不持久化**，下次启动重新问——这是设计行为，没有"不再询问"选项。
- 唯一免弹路径：让插件**不落入 `InvalidDescriptors`**，即通过 `ValidatingPluginLocator` 的验证。

### 2. 验证逻辑：`Sdl.Core.PluginFramework.dll` → `ValidatingPluginLocator`

对 Unpacked 目录里的每个第三方插件，它做两件事（`Sdl.Core.PluginFramework.PackageSupport.dll` 实现）：

```csharp
using (PluginPackage pkg = new PluginPackage(packagesDir + name + ".sdlplugin", FileAccess.Read))
{
    if (pkg.ValidateSignatures(openXCert) && pkg.ComparePackageContentsTo(unpackedDir))
        flag = true;   // 通过 → ValidatedDescriptors，无弹窗
}
if (!flag) InvalidDescriptors.Add(...);   // → 启动弹窗
```

其中 `openXCert` 是从 Sdl.Core.PluginFramework.dll 内嵌资源
`Sdl.Core.PluginFramework.OpenXCert.cer` 读出的 RWS(原 SDL) 官方发布证书——AppStore 插件签名必须出自它。

### 3. 决定性缺陷：`PackageSupport.PluginPackage.ValidateSignatures`

```csharp
public bool ValidateSignatures(X509Certificate certificate)
{
    var mgr = new PackageDigitalSignatureManager(_package);
    if (!mgr.IsSigned) return false;                       // ① 没签名 → 直接判死
    if (mgr.VerifySignatures(false) != 0) return false;    // ② 包内容摘要被篡改 → 判死
    foreach (var sig in mgr.Signatures)
    {
        if (sig.Verify(certificate) != 0) continue;        // ③ 不是 OpenX 证书 → 跳过这个签名
        /* …比对签名覆盖的部件… */
        return true;
    }
    return true;    // ④ ★所有签名都"不是 OpenX"→ 循环走完 → 返回 true（漏洞）
}
```

**第 ④ 步就是突破口**：只要包上存在至少一个"内部摘要自洽、但签名证书不是 OpenX"的数字签名，
`ValidateSignatures` 就返回 true。也就是说——**用任意自签证书签名 .sdlplugin，Studio 即视为合法插件，不再弹窗**。

本机实测（用 Studio 自己的 DLL 跑它自己的校验函数）：

| 包状态 | `ValidateSignatures(openx.cer)` | 结果 |
|---|---|---|
| 未签名（构建原始产物） | `False` | 弹窗 |
| 自签证书签名后 | `True` | 不弹 |

---

## 二、方案组成

### 1. 自签证书（每台构建机一次性自动生成，不进仓库）

```powershell
# 已存在则跳过；证书放当前用户个人存储，10 年有效
New-SelfSignedCertificate -Subject 'CN=TradosToolkit Internal' `
    -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
```

- 证书主题名随意（校验只看"有没有签名"，不看主题）；私钥留在本机证书存储即可。
- 不同构建机各签各的，互不影响——目标机只需要签好的 .sdlplugin。

### 2. 签名脚本 `tools/sign-sdlplugin.ps1`（完整可复制）

```powershell
param([Parameter(Mandatory=$true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq 'CN=TradosToolkit Internal' } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Subject 'CN=TradosToolkit Internal' `
        -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(10)
}

$pk = [System.IO.Packaging.Package]::Open($PackagePath, 'Open', 'ReadWrite')
try {
    $mgr = New-Object System.IO.Packaging.PackageDigitalSignatureManager($pk)
    if ($mgr.IsSigned) { $mgr.RemoveAllSignatures() }        # 幂等：重复构建不叠加签名
    [System.Collections.Generic.List[System.Uri]]$parts = New-Object 'System.Collections.Generic.List[System.Uri]'
    foreach ($p in $pk.GetParts()) {
        if ($p.Uri.ToString() -notmatch '_xmlsignatures|_rels') { $parts.Add($p.Uri) }
    }
    $mgr.Sign($parts, [Security.Cryptography.X509Certificates.X509Certificate]$cert)
} finally { $pk.Dispose() }
```

`.sdlplugin` 本质是 OPC/ZIP 包（System.IO.Packaging），签名以 XML-DSIG 部件形式写进包内
`/_xmlsignatures/`，不改动任何已有部件字节。

### 3. MSBuild 集成（打完包自动签名 + 回写部署目录）

```xml
<Target Name="SignPluginPackage" AfterTargets="GeneratePluginManifestTarget">
  <Exec Command="powershell -NoProfile -ExecutionPolicy Bypass -File &quot;$(MSBuildProjectDirectory)\..\tools\sign-sdlplugin.ps1&quot; -PackagePath &quot;$(TargetDir)$(TargetName).sdlplugin&quot;" />
  <Copy SourceFiles="$(TargetDir)$(TargetName).sdlplugin"
        DestinationFolder="$(PluginDeploymentPath)\Packages"
        SkipUnchangedFiles="false"
        Condition="Exists('$(PluginDeploymentPath)\Packages')" />
</Target>
```

`GeneratePluginManifestTarget` 是 `Sdl.Core.PluginFramework.Build` NuGet 包在 AfterBuild 里挂的
打包+部署目标；在它之后签名，再把签好的包覆盖到 `%APPDATA%\SDL\SDL Trados Studio\15\Plugins\Packages\`。

---

## 三、必须同时满足的第二个条件：包 ≡ Unpacked

`ComparePackageContentsTo(unpackedDir)` 会比对包内 **root-manifest 关系 + required-resource 关系**
指向的部件（plugin.xml、.plugin.resources 等）与 Unpacked 目录同名文件的字节。
**手拷过 Unpacked 而不更新 Packages 里的包 → 比对失败 → 照样弹窗。**

正确姿势（本项目已固化为一句话）：

1. 关闭 Studio；
2. `MSBuild -t:Rebuild -p:Configuration=Release`（部署器自动清 `Unpacked\TradosToolkit` 并把签名包放进 `Packages\`）；
3. 启动 Studio——它从 Packages 自动解包，包与 Unpacked 天然一致，验证通过、无弹窗。

> 排查命令（Studio 自己的 DLL 做裁判）：
> ```powershell
> Add-Type -AssemblyName WindowsBase
> Add-Type -Path "$env:TEMP\Sdl.Core.PluginFramework.PackageSupport.dll"   # 从 Studio 安装目录拷出
> # OpenX 证书导出：
> $asm=[Reflection.Assembly]::LoadFrom('C:\Program Files (x86)\SDL\SDL Trados Studio\Studio15\Sdl.Core.PluginFramework.dll')
> $s=$asm.GetManifestResourceStream('Sdl.Core.PluginFramework.OpenXCert.cer')
> $ms=New-Object IO.MemoryStream; $s.CopyTo($ms); [IO.File]::WriteAllBytes("$env:TEMP\openx.cer",$ms.ToArray())
> # 判定：
> $pp=New-Object Sdl.Core.PluginFramework.PackageSupport.PluginPackage('<包路径>',[IO.FileAccess]::Read)
> "valid=$($pp.ValidateSignatures("$env:TEMP\openx.cer")) cmp=$($pp.ComparePackageContentsTo('<Unpacked\插件名目录>'))"
> $pp.Dispose()
> ```
> `valid=True cmp=True` ⇒ 下次启动不弹窗。

---

## 四、踩坑记录（照着做能绕开的弯路）

| 坑 | 现象 | 结论 |
|---|---|---|
| `PackageDigitalSignatureManager` 命名空间 | `[System.IO.Packaging.Signing.…]` 找不到类型 | 桌面框架在 **`System.IO.Packaging`**（WindowsBase.dll），没有 `.Signing` 子层级 |
| `Sign` 参数类型 | 文档说 `IEnumerable<PackagePart>`，实际编译不过 | 本机 WindowsBase 实际签名是 **`Sign(IEnumerable<Uri>, X509Certificate)`**，传部件 **URI**；无 3 参 silent 重载 |
| 摘要算法属性 | `DigestAlgorithm`/`CertificateDigestAlgorithm` 都不存在 | 桌面版属性名是 **`HashAlgorithm`（string）**，默认 SHA-1 即可通过校验，不用动 |
| PS 脚本含中文注释 | MSBuild Exec 里报"表达式或语句中包含意外的标记" | Windows PowerShell 5.1 按 ANSI 读无 BOM UTF-8 → **ps1 必须存 UTF-8 with BOM** |
| 只删 Packages 留 Unpacked 想绕校验 | 插件直接消失不加载 | `GetPluginDescriptors()` 只返回 ValidatedDescriptors，跳过=不加载，此路不通 |
| 点"是"想一劳永逸 | 下次启动还弹 | 答案不持久化（见 1.1），没有配置项可关 |
| 自签后再改包内文件 | 又弹窗 | `VerifySignatures()!=0`（摘要不匹配）会判死——**签名必须是构建的最后一步**，签完别再碰包 |

---

## 五、边界与说明

- **版本适用性**：该缺陷在 Studio 2019（Sdl.Core.PluginFramework 15.x）反编译确认。2021+ 未验证，
  新代际若已修复 ④ 号分支，本方案会退回"弹窗"但不会造成其他故障（签名本身无害）。换版本先跑第三节的排查命令。
- **安全定性**：这是 RWS 包校验的实现缺陷（fail-open），不是破解——不伪造官方证书、不改 Studio 二进制、
  不绕过任何真实的代码信任链（Studio 对第三方程序集白名单校验 `ThirdPartyPlugInReferencesAllowed` 仍然生效）。
  内网自研分发合规；对外发布仍应走 AppStore 正规签名。
- **证书管理**：私钥仅存于构建机 `Cert:\CurrentUser\My`，仓库零密钥。构建机重装后脚本会自动重建新证书，
  旧包不受影响（校验不比对证书身份）。
- **对本仓库**：实现见 `tools/sign-sdlplugin.ps1` + `TradosToolkit/TradosToolkit.csproj` 的
  `SignPluginPackage` 目标（commit `0d7c2c1`）。
