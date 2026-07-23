# Pinta Windows 开发环境安装指南

## 前置条件：安装 MSYS2（必装）

### 1. 下载并安装 MSYS2

1. 访问：https://www.msys2.org/
2. 下载安装包 `msys2-x86_64-*.exe`
3. 双击安装，默认安装到 `C:\msys64`

### 2. 安装 GTK 依赖（在 MSYS2 Clang64 终端运行）

1. 从开始菜单打开 "MSYS2 Clang64"（必须是 Clang64，不是 MSYS 或 UCRT64）
2. 运行以下命令：

```bash
# 先更新系统包
pacman -Syu

# 如果提示重启 terminal，关闭 MSYS2，重新打开 "MSYS2 Clang64" 后继续
pacman -Su

# 安装 Pinta 需要的依赖（和 CI 配置一致）
pacman -S --needed --noconfirm \
  mingw-w64-clang-x86_64-gtk4 \
  mingw-w64-clang-x86_64-libadwaita \
  mingw-w64-clang-x86_64-webp-pixbuf-loader
```

### 3. 验证 MSYS2 安装是否正确

在 Windows PowerShell 中运行：
```powershell
Test-Path 'C:\msys64\clang64\bin\libgtk-4-1.dll'
```
如果返回 `True`，表示安装路径正确。

---

## 重新编译并运行

现在你的机器上有 MSYS2 了，重新编译：

```powershell
cd g:\AISpine\Pinta
.\dev.ps1 -Clean -Run
```

或者双击 `run.bat`
