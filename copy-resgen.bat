@echo off
:: 把 ResGen.exe 复制到 .NET Framework 目录，让 Trados 插件打包任务能找到它
copy /Y "C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\ResGen.exe" "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ResGen.exe"
if exist "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\ResGen.exe" (
    echo RESGEN_COPIED_OK
    timeout /t 3
) else (
    echo RESGEN_COPY_FAILED
    timeout /t 10
)
