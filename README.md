<img src="icon.png" width="96" alt="icon">

> 游戏：吉星派对（Astral Party）　插件名：对队友进行大调查　｜　作者：TenkinMori
>
> CesiumLoader 托管模组：把 `TeammateCheckMod.dll` 放进 `AstralParty_ModLoader/mods/TeammateCheckMod/` 即可。

# 对队友进行大调查（CesiumLoader 版）

## 作用

两件事，都在你自己的客户端里完成，不改服务端数据、也不伪造内容：

1. **撕掉名片的遮挡**：对手勾了「隐藏资料」「隐藏战绩」时，照常显示他的统计数据与对局记录。
2. **开局查组队**：进房间后静默把同房间每个人的近期战绩过一遍，统计两两同场次数
   （**本局算一次**），累计 ≥3 次、或者出现过**连续两局**同场，就判为疑似组队，
   在局内右下角弹出小面板，同时写进日志。

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

装好即生效，不用按键也能看结果。

- 打开任意玩家名片 → 对方隐藏的资料/战绩会照常显示
- 进入房间（含联机房间）后：右下角出现「疑似组队」小面板，每行只有一句话，形如

  ```
  疑似组队
  小明 和 小红 疑似组队
  阿强 和 小明 疑似组队
  ```

- **拖动**：按住面板任意位置就能拖，松手后会自动收回屏幕内（位置会一直保留，不会被重新摆回右下角）
- **点一下面板**：在旁边展开「房间成员」列表（比主面板窄一些），列出本房间所有真人（自己带「（我）」）；
  再点一下收起。点列表里的名字 → 直接打开他的名片（走游戏自己的看名片流程，
  所以隐藏的资料/战绩也会按本模组的效果展开）
- **F9**：一键隐藏 / 显示**所有**自绘面板（疑似组队 + 成员列表），再按一次恢复。
  默认 `F9`，可在配置里改
- 没有命中时面板显示「暂未发现疑似组队」，但仍然可以点开成员列表

配置文件 `AstralParty_ModLoader/configs/TeammateCheckMod.json`（首次启动自动生成）：

```json
{
  "enabled": true,
  "minTogether": 3,
  "maxRecords": 20,
  "showPanel": true,
  "toggleKey": "F9"
}
```

| 键 | 默认 | 说明 |
| --- | --- | --- |
| `enabled` | `true` | 总开关 |
| `minTogether` | `3` | 同场累计多少次（本局也算一次）算疑似组队；连续两局同场不受此值限制 |
| `maxRecords` | `20` | 每人往回查多少局 |
| `showPanel` | `true` | 启动时是否显示面板（关掉后只写日志，F9 仍可唤出） |
| `toggleKey` | `"F9"` | 隐藏/显示所有面板的快捷键，写 `KeyCode` 名（如 `F9`、`Delete`、`O`）；`None` 表示不用快捷键 |

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
3. 对房间里的人两两统计：有多少场对局的参与者同时包含这两个 UID，**再加上本局**这一次；
4. 连场单独判：某人自己的战绩里**相邻两局**都有对方，或者他的**最近一局**就有对方而这一局又同场
   （两人任一方向成立即可）；
5. `累计 ≥ minTogether` 或 `有连场` 的组合判定为疑似组队，按次数降序显示（最多显示 8 行）。

面板是用 FairyGUI 在代码里搭的（照 `TownTrainer` 的做法）：两个 `opaque = true, draggable = true`
的 `GComponent` 直接挂到 `GRoot`，各自带 `sortingOrder = 30000`，所以不会被游戏窗口盖住。

几条踩过的坑，改面板时别踩回去：

- 底板必须 `opaque` —— `Container.HitTest_Container` 只在 `opaque && 内容矩形命中` 时才把触摸算回组件，
  不 opaque 就只有文字那几行能点、能拖。
- **不能再套一层 `touchable = false` 的全屏图层** —— `DisplayObject.InternalHitTest` 对不可触摸的对象
  直接返回 `null`，子对象连试都不试，表现就是「看得见、拖不动、点不动」。
- 背景 `GGraph` 必须在面板尺寸确定之后画一次（`DrawRoundRect` 内部才 `SetSize`）；尺寸没变就跳过绘制，
  背景会一直是 0×0，表现就是「面板没背景」。所以布局里先 `SetSize` + `DrawRoundRect`，
  再用标志位记住初始位置，避免每帧把用户拖出来的位置按回去。
- 用 `sortingOrder` 而不是 `SetChildIndex` —— FairyGUI 的 `SetChildIndex` 对排序子对象直接 `return`。

几个实现细节：

- 5154 不用 `AccountLogic.RequestGetShowPlayerC2S`（那个成功后会弹名片窗口），直接
  `RPC.GetShowPlayerC2S.GetShowPlayerC2SCall(...)`，再从私有字段 `AccountLogic.playerCareer`
  里读结果。服务端回调先于 `RPCAsyncResult.Finish()` 执行，所以 `OnFinished` 里读得到新数据。
- 5155 用现成的 `AccountLogic.RequestGetPlayerFightRecordC2S(playerId, index)`，它不弹窗。
- 名片窗口开着时整个扫描暂停（`paused`），两个自绘面板也临时让开，避免两边抢同一个 `playerCareer`。
- 每人一份缓存（30 分钟），换房间/换局时只补查名单里的新面孔；4 人满员时首次约 4×(1+20) 次请求。
- 点成员列表里的一行，调的是 `AccountLogic.RequestGetShowPlayerC2S(id, nick, lv, headIcon, labelData, null)`
  —— 和游戏自己的 `UICom_RoomPlayer.ShowAccountInfo()` 同一条路子（看自己时按游戏约定传 `0`）。

### 为什么是「改标志位 + 裸协议」而不是别的

游戏是 IL2CPP + HybridCLR 热更程序集，没有 Harmony 这类托管 detour，CesiumLoader 也只提供
配置/日志/反射层面的能力。好在隐藏判定只是两个 bool、战绩详情本来就有公开的请求方法，
不需要替换任何方法体。

## 验证

- 启动日志：`配置: enabled=True，minTogether=3，maxRecords=20，showPanel=True，toggleKey=F9`
  以及 `字段: 名片窗口=True，名片数据=True，统计刷新=True，战绩刷新=True`
- 打开一个隐藏了资料的玩家名片：`名片：已强制展开对方隐藏内容（统计=有，战绩=N 条，刷新=True/True）`
- 关掉名片：`名片已关闭：隐藏开关已还原。`
- 进房间后：面板首显一次 `浮窗已显示（右下角，可拖动，点击展开成员，sortingOrder=30000）。`，
  扫完是 `扫描完成：本人群 4 人，命中 M 场对局记录。`，有命中时逐行
  `小明 和 小红 疑似组队`
- 点面板：`成员列表已收起。`（收起时）；点名单里的人：`正在查看名片：<昵称>`
- 按 F9：`面板已隐藏（F9 再按一次显示）。` / `面板已显示（F9 再按一次隐藏）。`
- 若出现 `服务端没有下发数据`，说明该玩家名下数据确实被服务端裁掉（端上无权可解）。

## 构建

需要 .NET SDK 9（`dotnet build` 目标 `netstandard2.0`）。

```powershell
./build.ps1
```

产物：

- `dist/TeammateCheckMod/{TeammateCheckMod.dll,TeammateCheckMod.json,icon.png}`
- `dist/TeammateCheckMod-1.2.1.zip`

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

### 1.2.1

- 修**面板拖不动、也没有背景**：去掉外层 `touchable = false` 的全屏图层
  （`DisplayObject.InternalHitTest` 对不可触摸的对象直接返回 `null`，子面板永远收不到触摸），
  两个面板改为直接挂 `GRoot` 并各自带 `sortingOrder`（照 `TownTrainer` 的做法）
- 背景 `GGraph` 改为尺寸确定后调 `DrawRoundRect`，不再因为「尺寸没变」跳过绘制而留下 0×0 的空板
- 面板整体放大：主面板宽度 300 → 440，标题 21 号字、正文 18 号字、行高 26；
  成员列表 220 → 320 宽，行高 36，圆角底板
- 拖动后的位置会保留（不再每帧被摆回右下角）

### 1.2.0

- 右下角面板改成**可拖动**，松手自动收回屏幕内
- **点一下面板**展开「房间成员」列表，点名字直接在局内打开他的名片（走游戏自己的看名片流程）
- 新增快捷键 `toggleKey`（默认 **F9**），一键隐藏/显示**所有**自绘面板
- 面板文字从简：每行只说「A 和 B 疑似组队」，次数/连场/进度不再显示在面板上（仍写日志）
- 名片窗口打开时两个面板自动让开，关掉后自动回来（不改总开关状态）

### 1.1.0

- 新增「开局组队检测」：静默扫描当前房间每人的近期战绩，两两统计同场次数，
  累计（含本局）≥ `minTogether`（默认 3）次、或有连续两局同场即判疑似组队，
  结果写进局内右下角浮窗与日志
- 新增配置 `minTogether` / `maxRecords` / `showPanel`
- 查询走裸 5154 + `RequestGetPlayerFightRecordC2S`，不弹名片窗口；每位玩家缓存 30 分钟
- 浮窗改为 `sortingOrder = 30000` 的独立图层，不再被游戏窗口盖住；昵称里的 UBB 标记会剥掉

### 1.0.0

- 首次发布：解除名片上的「隐藏资料 / 隐藏战绩」遮挡

## 许可

MIT，见 [LICENSE](LICENSE)。
