# SprocketLaserRangefinder

适用于《Sprocket》的 MelonLoader 激光测距与自动装表原型模组。它读取炮镜准星处的距离和当前火炮数据，计算低伸弹道，并通过游戏原生火炮控制链抬高炮管。

## 功能

- 使用 `SprocketDepth` 获取炮镜准星中心的距离。
- 有效测距范围为 20 米至深度纹理上限（当前约 5000 米）。
- 从游戏中读取当前火炮、炮弹、炮口初速、重力和阻力曲线等数据。
- 按照 Sprocket 的弹道模型计算低伸解、装表角和飞行时间。
- 将角度修正注入原生 `GunLayer` 瞄准流程，保留游戏的转速、加速度、射界、损伤和乘员效率限制。
- 在炮镜上方中央显示距离、装表角、飞行时间和载车运动补偿状态。

## 按键

- 左 `Ctrl`：测距并自动装表。
- `Z`：清除当前测距和装表结果。
- `L`：开关载车运动补偿，默认关闭。

退出炮镜后会保留测距和装表结果；切换场景时会清除。`L` 只补偿开火车辆自身的运动，不会跟踪目标，也不会计算移动目标提前量。

## 安装

1. 安装与游戏版本匹配的 MelonLoader。
2. 将 `SprocketLaserRangefinder.dll` 放入游戏根目录的 `Mods` 文件夹。
3. 将 [`SprocketDepth.dll`](https://github.com/furryaxw/SprocketDepth) 放入游戏根目录的 `UserLibs` 文件夹。

当前目标环境为 Sprocket `0.2.53.1`、MelonLoader `0.7.2/net6` 和 Windows D3D11。

## 构建

项目目标框架为 .NET 6，并引用本地 Sprocket MelonLoader/IL2CPP 程序集和 `SprocketDepth.dll`。默认目录布局为：

```text
G:\Sprocket\
├── MelonLoader\
├── UserLibs\SprocketDepth.dll
└── mod\SprocketLaserRangefinder\
```

```powershell
dotnet build .\SprocketLaserRangefinder\SprocketLaserRangefinder.csproj `
  --configuration Release
```

默认构建会将 DLL 部署到 `Mods`。使用 `-p:SkipModDeploy=true` 可以只生成 DLL；仓库位于其他位置时可通过 `-p:SprocketGameRoot="G:\Sprocket"` 指定游戏根目录。

## 当前限制

- 当前版本为 Prototype，尚未完成所有车辆和距离下的游戏内验收。
- 不保存或跟踪世界坐标，不显示预计落点。
- 没有横向稳定器的车辆可能无法稳定执行载车运动补偿产生的横向修正，此时可按 `L` 关闭补偿。

## License

[GPL-3.0-only](LICENSE.txt)
