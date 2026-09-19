# 现成插件包（免构建直接用）

`TradosToolkit.sdlplugin` = 最新构建 + 已自签（不会弹"未认证插件"，机制见 ../docs/SDL_PLUGIN_SIGNING.md）。

## 安装到目标机（需已装 Studio 2019 + .NET Framework 4.8）

1. 把 `TradosToolkit.sdlplugin` 拷到目标机的
   `%APPDATA%\SDL\SDL Trados Studio\15\Plugins\Packages\`（目录不存在就新建）；
2. 启动 Studio，自动解包加载，无需点任何确认框；
3. 升级 = 关闭 Studio 后用新包覆盖同名文件再启动。

## 验证安装成功
- Home 功能区出现 TradosToolkit/工作台 组、左导航出现 Translation Center；
- 若插件没出现：看 `%LOCALAPPDATA%\SDL\SDL Trados Studio\15.0.0.0\SDL Trados Studio_<pid>.log`（UTF-16），
  常见原因是 RequiredProduct 与 pluginconfig.xml 不匹配（详见 ../docs/OFFLINE_DEPLOY.md）。

> 该文件由构建自动维护：csproj 的 `SignPluginPackage` 目标每次构建后会把最新签名包复制到这里，随代码一起提交即可。
