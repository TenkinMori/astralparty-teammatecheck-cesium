<img src="icon.png" width="96" alt="icon">

> 游戏：吉星派对（Astral Party）　插件名：对队友进行大调查　｜　作者：TenkinMori
>
> CesiumLoader 托管模组：把 `TeammateCheckMod.dll` 放进 `AstralParty_ModLoader/mods/TeammateCheckMod/` 即可。

# 对队友进行大调查（CesiumLoader 版）

## 作用

对手在名片上勾了「隐藏资料」「隐藏战绩」时，本模组在**你自己的客户端**里把这两层遮挡撕掉，
照常显示对方的统计数据与对局记录。

- 只改本地的显示判断，不改服务端数据、也不伪造任何内容
- 自己的名片不动：你自己的「隐藏」开关照常生效，别人仍然看不到你隐藏的东西
- 对方名片关掉后，开关按原值还原，不会污染后续的名片

## 安装

1. 确认游戏装有 CesiumLoader（`AstralParty_CN.exe` 同目录有 `version.dll` 与 `AstralParty_ModLoader`）。
2. 把 `TeammateCheckMod` 整个文件夹放到：

   ```
   <游戏目录>/AstralParty_ModLoader/mods/TeammateCheckMod/
   ```

   里面应有 `TeammateCheckMod.dll`、`TeammateCheckMod.json`、`icon.png`。
3. 启动游戏。`AstralParty_ModLoader/logs/cesium-loader-*.log` 里应出现一行 `TeammateCheckMod ok`。

## 使用

装好即生效，没有按键、也没有界面。

- 打开任意玩家名片 → 对方隐藏的资料/战绩会照常显示
- `AstralParty_ModLoader/configs/TeammateCheckMod.json`：

  ```json
  { "enabled": true }
  ```

  `enabled: false` 可临时关掉本模组（下一次巡检即还原）。

## 原理

### 数据一直在，只是没画

协议的「看名片」链路（协议号取自 `party.protocol`）：

| 方向 | 协议 | 内容 |
| --- | --- | --- |
| C→S | `GetShowPlayerC2S` (5153) | 请求某玩家名片 |
| S→C | `GetShowPlayerS2C` (5154) | `ShowPlayer`：玩家信息 + `IsShowData` / `IsShowFight` + `Statistics` + `Record[]` |

客户端收到后整份塞进 `GameLogic.PlayerCareer`：

```csharp
// GameLogic/PlayerCareer.cs —— UpdateData(GetShowPlayerS2C model)
isShowData  = model.ShowData.IsShowData;
isShowFight = model.ShowData.IsShowFight;
DealRecords(model.ShowData.Record);
statistics  = model.ShowData.Statistics;
```

真正决定「给不给看」的是 `UI.AccountInfoWindow` 里的渲染判断：

```csharp
// RefreshStatisticalData()，:544
if (IsSelf || playerCareer.isShowData) { /* 填统计 */ }
else { group_Statistical.visible = false; txt_StatisticalTip.text = 1035.GetLocal(...); }

// RefreshFightRecord(records)，:571
if (IsSelf || playerCareer.isShowFight) { list_FightData.numItems = records.Count; }
else { list_FightData.visible = false; txt_FightDataTip.text = 1035.GetLocal(...); }
```

`IsSelf` 之外的两个人用的是同一份 `PlayerCareer`，开关本身只是服务端存的两个 bool
（`SetShowPlayerC2S` 5151 上传），所以**界面上那层遮挡在本地完成**。

### 本模组做了什么

0.2 秒巡检一次 `UIManager._cachedAccountInfoWindow`：

1. 窗口在显示，且当前展示的不是自己的名片；
2. 把 `PlayerCareer.isShowData` / `isShowFight` 置为 `true`；
3. 反射调用私有的 `RefreshStatisticalData()` 与 `RefreshFightRecord(records)` 重画；
4. 名片关掉、换人或切回自己时，按打开前保存的值还原。

置位是有条件的：`statistics` / `Records` 为 `null` 时不置位，只记一条
`名片：对方隐藏了资料/战绩，但服务端没有下发数据` —— 也就是说，如果服务端确实没把数据发下来，
这里不会凭空造内容，日志会明确告诉你「不是模组没生效，是服务端没给」。

### 为什么是「改标志位」而不是别的

游戏是 IL2CPP + HybridCLR 热更程序集，没有 Harmony 这类托管 detour，CesiumLoader 也只提供
配置/日志/反射层面的能力。好在隐藏逻辑的判定全在这两个 bool 上，改状态即可，不需要替换方法体。

## 验证

- 启动日志：`[TeammateCheckMod] 配置: enabled=True，名片窗口字段=True，名片数据字段=True，统计刷新=True，战绩刷新=True`
- 打开一个隐藏了资料的玩家名片，日志出现
  `名片：已强制展开对方隐藏内容（统计=有，战绩=N 条，刷新=True/True）`
- 关掉名片：`名片已关闭：隐藏开关已还原。`
- 若出现 `服务端没有下发数据`，说明该玩家名下数据确实被服务端裁掉（端上无权可解）。

## 构建

需要 .NET SDK 9（`dotnet build` 目标 `netstandard2.0`）。

```powershell
./build.ps1
```

产物：

- `dist/TeammateCheckMod/{TeammateCheckMod.dll,TeammateCheckMod.json,icon.png}`
- `dist/TeammateCheckMod-1.0.0.zip`

`lib/CesiumLoader.SDK.dll` 取自游戏 `AstralParty_ModLoader/sdk`（3.8.1），
`refs/game` 是当前版本的游戏程序集，仅用于编译引用，不会打进 mod。

## 本版核对事实（2026-11，客户端 11777536 B 热更程序集）

- `UIManager._cachedAccountInfoWindow`：`UI/UIManager.cs:157`，`AccountInfo` 属性在 `:343` 懒构造
- `AccountInfoWindow.playerCareer`：`UI/AccountInfoWindow.cs:18`
- `RefreshData()` `:514`、`RefreshStatisticalData()` `:540`、`RefreshFightRecord(List<BattleShortRecord>)` `:565`
- `PlayerCareer.isShowData/isShowFight/statistics/Records`：`GameLogic/PlayerCareer.cs`
- 协议字段：`ShowPlayer.IsShowData`(7) / `IsShowFight`(8)，`ShowPlayerInfo` 同上

## 兼容性

- 游戏版本：2026-11 的 CesiumLoader 3.8.1 + 当前热更程序集
- 只用到 `UIManager` / `AccountInfoWindow` / `PlayerCareer` 的私有字段与私有方法名；
  哪天游戏改名，模组不会崩，只会在启动日志里提示布局变化并不生效

## 已知边界

- **服务端不下发就看不到**：模组只解除本地渲染遮挡，不伪造数据。若服务端对隐藏玩家裁掉了
  `Statistics` / `Record`，名片上仍然只有提示（日志会记一条说明）。
- 单场详情（`GetPlayerFightRecordC2S` 5155）是另一次请求，服务端若按隐藏开关拒绝，
  点头开详情会没有反应 —— 这属于服务端校验，本地无法绕。
- 只处理「看别人的名片」；队伍/结算里的其他展示入口不在本版范围内。

## 许可

MIT，见 [LICENSE](LICENSE)。
