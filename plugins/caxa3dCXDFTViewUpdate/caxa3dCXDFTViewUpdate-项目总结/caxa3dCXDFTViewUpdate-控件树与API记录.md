# caxa3dCXDFTViewUpdate-控件树与API记录

## 八、常用命令

```powershell
powershell -ExecutionPolicy Bypass -File C:\code\ICAPI\caxa3dmytoolsaddin\build.ps1

dotnet build C:\code\ICAPI\caxa3dmytoolsaddin\plugins\caxa3dCXDFTViewUpdate\caxa3dCXDFTViewUpdate.csproj -c Release -p:Platform=x64

powershell -ExecutionPolicy Bypass -File <插件目录>\register-crx.ps1
powershell -ExecutionPolicy Bypass -File <插件目录>\unregister-crx.ps1
```

CRX C++ 工程 `caxa3dCXDFTViewUpdate-crx\caxa3dCXDFTViewUpdate.vcxproj` 需 VS/C++ 工具链单独编译；
仓库里带的是预编译产物（127488 B）。改 `.cpp` 后必须重编并把新 `.crx` 放回 `caxa3dCXDFTViewUpdate-crx\bin\`，`build.ps1` 才会带进暂存目录。

