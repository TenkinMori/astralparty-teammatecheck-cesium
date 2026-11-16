<img src="icon.png" width="96" alt="icon">

> 游戏：吉星派对（Astral Party）　插件名：对队友进行大调查　｜　作者：TenkinMori
>
> CesiumLoader 托管模组：把 `TeammateCheckMod.dll` 放进 `AstralParty_ModLoader/mods/TeammateCheckMod/` 即可。

# 对队友进行大调查（CesiumLoader 版）

## 作用

两件事，都在你自己的客户端里完成，不改服务端数据、也不伪造内容：

1. **撕掉名片的遮挡**：对手勾了「隐藏资料」「隐藏战绩」时，照常显示他的统计数据与对局记录。
2. **开局查组队**：进房间后静默把同房间每个人的近期战绩过一遍，找出「近 20 局里一起玩过 ≥4 次」的组合，
   在局内左下角弹出小浮窗，同时写进日志。

- 自己的名片不动：你自己的「隐藏」开关照常生效，别人仍然看不到你隐藏的东西
- 别人名片关掉后，开关按原值还原，不会污染后续的名片
- 组队统计只读公开可见的战绩数据；每个玩家的结果缓存 30 分钟，换一局只补查新面孔

## 安装

1. 确认游戏装有 CesiumLoader（`AstralParty_CN.exe` 同目录有 `version.dll` 与 `AstralParty_ModLoader`）。
2. 把 `TeammateCheckMod` 整个文件夹放到：

   ```
   <游戏目录>/AstralParty_ModLoader/mods/TeammateCheckMod/
   ```

   里面应有 `TeammateCheckMod.dll`、`TeammateCheckMod.json`、`icon.png`。
3. 启动游戏。`AstralParty_ModLoader/logs/cesium-loader-*.log` 里应出现一行 `TeammateCheckMod ok`。

## 使用

装好即生效，没有按键。

- 打开任意玩家名片 → 对方隐藏的资料/战绩会照常显示
- 进入房间（含联机房间）后：左下角浮窗显示「疑似组队」名单，形如

  ```
  疑似组队 · 近 20 局同场
  小明 ↔ 小红   6 次
  阿强 ↔ 小明   4 次
  已扫描 4/4 人 · 命中 12 场
  ```

  正在扫时脚注变成进度，扫完没有命中则显示「近 20 局里没有重复同场」。
- 配置文件 `AstralParty_ModLoader/configs/TeammateCheckMod.json`（首次启动自动生成）：

  ```json
  {
    "enabled": true,
    "minTogether": 4,
    "maxRecords": 20,
    "showPanel": true
  }
  ```

  | 键 | 默认 | 说明 |
  | --- | --- | --- |
  | `enabled` | `true` | 总开关 |
  | `minTogether` | `4` | 近 N 局里同场多少次算疑似组队（>3 次） |
  | `maxRecords` | `20` | 每人往回查多少局 |
  | `showPanel` | `true` | 是否显示局内浮窗（关掉后只写日志） |

## 原理

### 隐藏：数据一直在，只是没画

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

本模组 0.2 秒巡检一次 `UIManager._cachedAccountInfoWindow`：

1. 窗口在显示，且当前展示的不是自己的名片；
2. 把 `PlayerCareer.isShowData` / `isShowFight` 置为 `true`；
3. 反射调用私有的 `RefreshStatisticalData()` 与 `RefreshFightRecord(records)` 重画；
4. 名片关掉、换人或切回自己时，按打开前保存的值还原。

置位是有条件的：`statistics` / `Records` 为 `null` 时不置位，只记一条
`名片：对方隐藏了资料/战绩，但服务端没有下发数据` —— 如果服务端确实没把数据发下来，
这里不会凭空造内容，日志会明确告诉你「不是模组没生效，是服务端没给」。

### 组队：战绩列表里没有队友，单场详情里才有

- 5154 的 `Record[]` 元素是 `ShowPlayerShortFight`，只有 `Time` / `Rank` / `HeroId` / `Index` / `MapType`，
  **没有同场玩家**。
- 单场详情走另一对协议：`GetPlayerFightRecordC2S` (5155，`PlayerId` + `Index`) →
  `GetPlayerFightRecordS2C` (5156，`RecordData` = `RepeatedField<PlayerFightData>`)，
  每个 `PlayerFightData` 带 `PlayerId`、`Name`、`HeroId`、`Rank`、`Slot`、`IsGiveUp` 等。

所以流程是：

1. 对房间里每个人（过滤 `IsBot` 与 `PlayerId <= 100000` 的 NPC）发一次裸 5154，拿到他最近
   `maxRecords` 局的 `Index`；
2. 逐条发 5155（间隔 0.12 s），把这一局的参与者 UID 收进「对局表」，
   键是 `Time + MapType`，同一局从不同人嘴里查出来会自然合并；
3. 对房间里的人两两统计：有多少场对局的参与者同时包含这两个 UID；
4. `≥ minTogether` 的组合判定为疑似组队，按次数降序显示。

几个实现细节：

- 5154 不用 `AccountLogic.RequestGetShowPlayerC2S`（那个成功后会弹名片窗口），直接
  `RPC.GetShowPlayerC2S.GetShowPlayerC2SCall(...)`，再从私有字段 `AccountLogic.playerCareer`
  里读结果。服务端回调先于 `RPCAsyncResult.Finish()` 执行，所以 `OnFinished` 里读得到新数据。
- 5155 用现成的 `AccountLogic.RequestGetPlayerFightRecordC2S(playerId, index)`，它不弹窗。
- 名片窗口开着时整个扫描暂停（`paused`），避免两边抢同一个 `playerCareer`。
- 每人一份缓存（30 分钟），换房间/换局时只补查名单里的新面孔；4 人满员时首次约 4×(1+20) 次请求。

### 为什么是「改标志位 + 裸协议」而不是别的

游戏是 IL2CPP + HybridCLR 热更程序集，没有 Harmony 这类托管 detour，CesiumLoader 也只提供
配置/日志/反射层面的能力。好在隐藏判定只是两个 bool、战绩详情本来就有公开的请求方法，
不需要替换任何方法体。

## 验证

- 启动日志：`配置: enabled=True，minTogether=4，maxRecords=20，showPanel=True`
  以及 `字段: 名片窗口=True，名片数据=True，统计刷新=True，战绩刷新=True`
- 打开一个隐藏了资料的玩家名片：`名片：已强制展开对方隐藏内容（统计=有，战绩=N 条，刷新=True/True）`
- 关掉名片：`名片已关闭：隐藏开关已还原。`
- 进房间后：先看到 `战绩：<昵称> 近 N 局可查。`，扫完是
  `扫描完成：本人群 4 人，命中 M 场对局记录。`；有命中时逐行
  `疑似组队：小明 ↔ 小红   6 次`
- 若出现 `服务端没有下发数据`，说明该玩家名下数据确实被服务端裁掉（端上无权可解）。

## 构建

需要 .NET SDK 9（`dotnet build` 目标 `netstandard2.0`）。

```powershell
./build.ps1
```

产物：

- `dist/TeammateCheckMod/{TeammateCheckMod.dll,TeammateCheckMod.json,icon.png}`
- `dist/TeammateCheckMod-1.1.0.zip`

`lib/CesiumLoader.SDK.dll` 取自游戏 `AstralParty_ModLoader/sdk`（3.8.1），
`refs/game` 是当前版本的游戏程序集，仅用于编译引用，不会打进 mod。

## 本版核对事实（2026-11，客户端 11777536 B 热更程序集）

- `UIManager._cachedAccountInfoWindow`：`UI/UIManager.cs:157`，`AccountInfo` 属性在 `:343` 懒构造
- `AccountInfoWindow.playerCareer`：`UI/AccountInfoWindow.cs:18`
- `RefreshData()` `:514`、`RefreshStatisticalData()` `:540`、`RefreshFightRecord(List<BattleShortRecord>)` `:565`
- `PlayerCareer.isShowData/isShowFight/statistics/Records/recordDetail`：`GameLogic/PlayerCareer.cs`
- `AccountLogic.playerCareer`：`GameLogic/AccountLogic.cs:45`；`RequestGetPlayerFightRecordC2S` `:307`
- 协议字段：`ShowPlayer.IsShowData`(7) / `IsShowFight`(8)，`ShowPlayerInfo` 同上；
  `ShowPlayerShortFight`：Time/Rank/HeroId/Index/MapType/ReplayId/Version；
  `PlayerFightData`：PlayerId/Name/Lv/Gold/HeroId/Slot/Rank/MapType/IsGiveUp/HeadIcon/Background/PlayerLevel
- 协议号：5151/5152（设置显示）、5153/5154（看名片）、5155/5156（单场战绩详情）
- 房间：`GameLogicManager.room.curRoomInfo`（`RoomLogic.cs:44`）、`IsInRoom`（`:46`）、
  `RoomInfo.Players`、`RoomPlayer.Id`/`IsBot`/`GetNick()`

## 兼容性

- 游戏版本：2026-11 的 CesiumLoader 3.8.1 + 当前热更程序集
- 只用到 `UIManager` / `AccountInfoWindow` / `PlayerCareer` / `AccountLogic` 的私有字段与方法名；
  哪天游戏改名，模组不会崩，只会在启动日志里提示布局变化并不生效

## 已知边界

- **服务端不下发就看不到**：模组只解除本地渲染遮挡，不伪造数据。若服务端对隐藏玩家裁掉了
  `Statistics` / `Record`，名片上仍然只有提示（日志会记一条说明）。
- 单场详情（5155）是另一次请求，服务端若按隐藏开关拒绝，点头开详情会没有反应 —— 这属于
  服务端校验，本地无法绕。同理，若某人的 `Record[]` 被裁空，他也无法参与组队比对。
- 组队检测依赖「同一局 `Time + MapType` 相同」来合并记录；两人战绩被裁、或其中一人没把某局
  存进本地记录时，这一局不会被算进去（只会少算，不会多算）。
- 只处理「看别人的名片」和「当前房间的组队比对」；队伍/结算里的其他展示入口不在本版范围内。

## 更新日志

### 1.1.0

- 新增「开局组队检测」：静默扫描当前房间每人的近期战绩，两两统计同场次数，
  ≥ `minTogether`（默认 4）次判疑似组队，结果写进局内左下角浮窗与日志
- 新增配置 `minTogether` / `maxRecords` / `showPanel`
- 查询走裸 5154 + `RequestGetPlayerFightRecordC2S`，不弹名片窗口；每位玩家缓存 30 分钟

### 1.0.0

- 首次发布：解除名片上的「隐藏资料 / 隐藏战绩」遮挡

## 许可

MIT，见 [LICENSE](LICENSE)。
