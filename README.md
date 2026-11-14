# NebulaResume

<p align="center">
  <img src="icon.png" width="120" alt="icon">
</p>

<p align="center">
  <b>Nebula on the Ship（星云）断线重连插件 · Disconnect &amp; reconnect add-on for Nebula on the Ship</b><br>
  配套服务端 / Server: <a href="https://github.com/SailingBoat-ovo/Fmpostor">SailingBoat-ovo/Fmpostor</a> · License: GPL-3.0
</p>

[中文](#中文) · [English](#english)

---

## 中文

掉线以后，用同一个房间号重新进入，就能回到原来的对局：**职业 / 幽灵职业 / 附加职业 / 位置 / 任务列表 / 生死** 全部恢复。全新玩家还可以在对局中途以中立观战者身份加入。

> ⚠️ **这是配套项目的一半。** 本插件只负责客户端；保槽、识别重连、会议闸门都在服务端 **[Fmpostor](https://github.com/SailingBoat-ovo/Fmpostor)**（Turbo-700.0 及以上）。只装插件、或者只换服务端，都不会生效。

### 一、使用方式

#### 1. 需要什么

| 项目 | 要求 |
|---|---|
| 模组 | Nebula on the Ship（星云）v3.4.2.1 |
| 服务端 | Fmpostor **Turbo-700.0** 或更新版本，`config.json` 里开启 `Reconnect.Enabled` |
| 房间 | **所有玩家**都装了本插件（尤其是房主——身体由房主生成） |

#### 2. 安装

```
把 NebulaResume.zip 原样放进：
  <游戏目录>/Addons/NebulaResume.zip

启动游戏后会自动编译到 <游戏目录>/Cache/Dll/，不需要自己构建。
更新插件时：替换 zip，并删除 Cache/Dll/ 里 NebulaResume 开头的文件。
```

想从源码打包：把 `src/` 里的内容（`addon.meta`、`Scripts/`、`Language/` 等）压成 zip，**文件放在 zip 根目录**，命名为 `NebulaResume.zip`。

#### 3. 服务端开启

在 Fmpostor 的 `config.json` 中：

```json
"Reconnect": {
  "Enabled": true,
  "GracePeriodSeconds": 45,
  "EnableSpectator": false,
  "MaxSpectators": 3
}
```

#### 4. 掉线后怎么回去

1. 掉线后会弹出游戏原生的断线提示，插件会在里面加一句"重新进入本服务器即可回到这一局"。先记下本局的**房间号**（大厅界面一直显示）。
2. 回到主菜单 → 进入同一个服务器 → 「输入房间号」→ 进入。
3. 回来后：职业、位置、任务、生死都会还原，结算界面显示「已重连」而不是「掉线」。

注意：

- **会议进行中不能重连。** 会被直接拒绝，**不会**用掉你的保槽，会议结束后再进即可。失败的尝试不计入重连次数。
- 保槽有时间限制（默认 45 秒，由服主设置），超时后只能等下一局。
- 被踢出或封禁不会保槽。

#### 5. 房间设置（模组设置面板，分组「断线重连」）

| 设置项 | 默认 | 说明 |
|---|---|---|
| 未死亡重连后保持活人 | 开 | 关闭时，活着掉线的人回来后按灵魂处理 |
| 每局最多重连次数 | 3 | 0~10；超出后拒绝重连，需等本局结束 |
| 允许对局中观战加入 | 关 | 开启后，新玩家可以中途以观战者身份加入 |
| 观战人数上限 | 2 | 观战者与玩家共用 32 个玩家位 |

固定规则：

```
掉线前已死亡      →  回来是灵魂
全新玩家中途加入  →  中立观战职业 + 灵魂，不属于任何阵营，不影响胜负
```

#### 6. 排查

插件日志在 `<游戏目录>/BepInEx/LogOutput.log`，所有行都以 `[NebulaResume]` 开头。几条关键日志：

| 日志 | 含义 |
|---|---|
| `恢复完成 服务端playerId=…` | 状态包已应用 |
| `职业同步: 已向房主请求全场职业` → `职业同步: playerId=… 职业Id=…` | 重连者拿到了其他人的真实职业（房主那边会有 `已把 N 名玩家的职业发给 …`） |
| `状态包丢弃: 会议进行中 → 本次重连已取消` | 会议期间被拒，会后再进（游戏里也会弹窗提示） |
| `失效玩家清扫: playerId=…` | 旧身份的残留已被清理 |

---

### 二、实现方法

#### 1. 分工：服务端只做容器，模组状态只在客户端解释

NOS 的职业、附加职业等状态是**每台客户端各存一份**的，服务端只看得到原版的职业枚举。所以：

- **客户端（本插件）**：把每个玩家的 NOS 状态编码成 `int[]`，周期性上报；重连时把状态包还原到本地。
- **服务端（Fmpostor）**：把状态包当作**不透明字节**存起来、转发，不解析内容；负责保槽、判断谁是重连者、会议闸门、通知房主生成身体。

好处是 NOS 升级后服务端不需要改，只要插件跟上即可。

#### 2. 一次重连的完整流程

```
 ① 对局中     每个客户端每秒上报一次本机看到的全场状态（kind=1）
              服务端按玩家缓存最新的一份
 ② 掉线       服务端保留该玩家的位置（GracePeriodSeconds），快照随之冻结
 ③ 重新加入   服务端按身份（好友码 / PUID）认出他 → 放行进入已开始的对局
              会议中则直接拒绝，不动保槽
 ④ 生成身体   服务端给房主发补生成指令（kind=4）；房主客户端为他生成新身体
              （新的 PlayerId）。服务端从不创建或序列化角色
 ⑤ 清理旧身份 服务端移除旧身份并销毁旧身体
 ⑥ 续局       身体就位后，服务端给重连者发续局信号（kind=3）+ 状态包（kind=2）
 ⑦ 还原       插件在本地恢复：职业（连同参数广播给全场）、幽灵职业、附加职业、
              生死、任务、位置；再向房主请求全场职业（kind=11/12）
 ⑧ 确认       连接稳定 ResumeConfirmSeconds 秒后，服务端才真正用掉保槽；
              在此之前再次掉线，保槽原样还回去
```

#### 3. 协议

走 NOS 自带的 RPC 通道（原版 `RpcCalls = 128`），不需要额外补丁。帧格式：

```
[processHash:int32][count:int32][count × int32]
```

帧内第一个 int 是 kind：

| kind | 方向 | 内容 | 含义 |
|---|---|---|---|
| 1 | 客户端 → 服务端 | `[1, playerId, …状态]` | 上报状态快照 |
| 2 | 服务端 → 客户端 | `[2, playerId, …状态]` | 下发状态快照 |
| 3 | 服务端 → 客户端 | `[3, playerId]` | 续局信号 |
| 4 | 服务端 → 房主 | `[4, clientId, playerId]` | 补生成身体（`-1` = 用新的空闲 id） |
| 5 | 客户端 → 服务端 | `[5, playerId]` | 上报"我是死者"（周期重发） |
| 6 | 服务端 → 全体 | `[6, N, clientId…]` | 死亡名册（全量） |
| 7 | 服务端 → 客户端 | `[7, 0/1]` | 观战判定 |
| 8 | 服务端 → 全体 | `[8, N, clientId…]` | 观战名单（全量） |
| 9 | 房主 → 服务端 | `[9, 版本, 能力标志, 观战上限]` | 房间能力声明（bit0 重连、bit1 观战） |
| 10 | 房主 → 全体 | `[10, 会议中, 会议计时ms, 紧急冷却ms]` | 会议状态；服务端据此开关会议闸门，中途加入者据此对齐紧急按钮计时 |
| 11 | 重连者 → 房主 | `[11, 请求者playerId]` | 请求全场职业 |
| 12 | 房主 → 重连者 | `[12, 目标playerId, 对象playerId, …状态]` | 逐人回复职业 |

状态快照（v3）：

```
[0]  版本 = 3
     主职业 Id / 参数个数 / 参数…
     幽灵职业 Id / 参数个数 / 参数…
     附加职业个数 / 逐项（Id / 参数个数 / 参数…）
     是否死亡 (0/1)
     是否有位置 (0/1)，X, Y（坐标 ×1000 的整数）
     任务个数 N，N 组 [任务Id][类型Id][是否完成]
```

服务端不解析快照，也不假设长度固定。

#### 4. 几个关键难点

- **新身份而不是旧身份**：原版客户端无法把一具已存在的身体交还给新连接，所以回来的人用新的 PlayerId，旧身份由服务端清理。状态按"人"而不是按 PlayerId 恢复。
- **中途加入者不知道别人的职业**：NOS 只在开局分配一次职业。重连者先给其他人装占位职业，防止空引用；然后向房主请求全场真实职业，只在本机应用（`RoleSync`）。
- **重连者自己的职业**：带参数广播给全场，否则别人眼里他是白板船员，胜负判定也会出错。
- **会议**：会议中加入必然失败（会议界面先于身体同步）。所以服务端在会议期间直接拒绝且不动保槽；房主每秒广播会议状态（kind=10），服务端以此判断会议真正结束（NOS 取消了原版的投票结束消息）。
- **位置**：采集时取玩家**自己身体**的位置；会议、放逐动画、重生选点期间冻结为会前位置，避免回来时站在会议桌旁。
- **会议后的同步**：中途加入的客户端没有参与开局，缺外观缓存（`CosmeticsWarmup`）、紧急会议计时（`EmergencySync`），也缺会后流程需要的确认（`SyncAssist`），都要补齐。
- **残留对象**：旧身份的身体被销毁后，NOS 可能仍把它当在场玩家，每帧读一个已销毁对象的位置，导致原生崩溃级异常。`StalePlayerSweep` 会把这类玩家标记为掉线。

#### 5. 源码结构

```
src/
  addon.meta              插件元数据
  CustomServer.json       预置服务器
  Language/               中 / 英文本
  Scripts/
    Entry.cs              入口、心跳、恢复主流程
    NebulaStateWire.cs    RPC 收发与帧分派
    NebulaStateCodec.cs   状态采集 / 编码 / 还原
    ResumeTransition.cs   续局时的场景切换与本地重建
    HostBodySpawner.cs    房主端：按指令为重连者生成身体
    RoleSync.cs           重连者向房主同步全场职业（kind=11/12）
    SpawnGuard.cs         中途生成的玩家立即注册进 NOS，补占位职业
    StalePlayerSweep.cs   清理身体已销毁的残留玩家
    MeetingFlag.cs        房主广播会议状态（kind=10）
    EmergencySync.cs      对齐紧急会议计时
    SyncAssist.cs         补齐会后流程所需的确认
    CosmeticsWarmup.cs    补齐外观缓存（会议投票格）
    DeadRoster.cs         死亡名册（kind=5/6）
    SpectatorSeat.cs / SpectatorRole.cs   观战席与中立观战职业
    RoomCapability.cs     房间能力声明（kind=9）
    ReconnectPrompt.cs    断线 / 拒绝时的提示
    ReconnectStateTag.cs  结算状态"已重连"
    FrameGuard.cs         重连窗口内的帧级异常保护
    ResumeOptions.cs      设置项
    ……
NebulaResume.zip          成品包，直接放进 Addons
```

### 已知限制

- 房主掉线后，原房主身份无法交还（需要新房主接手），单人房无法恢复。
- 与 NOS 的兼容性依赖其内部 API，NOS 大版本更新后插件可能需要适配。
- 状态每秒上报一次，掉线前最后不到一秒内的职业变化可能丢失。

### 声明

本插件是独立 addon，不包含、不修改、不分发 NOS 模组本体。Among Us 及相关素材版权归 Innersloth LLC 所有。

### 许可

GNU General Public License v3.0 —— 见 [LICENSE](LICENSE)。

---

## English

Drop out, rejoin with the same room code, and you are back in the round you left, with your **role / ghost role / modifiers / position / task list / alive-or-dead state** all restored. Brand-new players can also join a running round as neutral spectators.

> ⚠️ **This is one half of a pair.** This add-on is the client side only. Slot reservation, reconnect detection and the meeting gate live in the server **[Fmpostor](https://github.com/SailingBoat-ovo/Fmpostor)** (Turbo-700.0 or newer). Installing only the add-on, or only the server, does nothing.

### 1. Usage

#### Requirements

| Item | Requirement |
|---|---|
| Mod | Nebula on the Ship v3.4.2.1 |
| Server | Fmpostor **Turbo-700.0** or newer, with `Reconnect.Enabled` turned on in `config.json` |
| Room | **Every player** has the add-on installed, especially the host, since the host spawns the body |

#### Install

```
Put NebulaResume.zip, unchanged, into:
  <game folder>/Addons/NebulaResume.zip

It is compiled automatically into <game folder>/Cache/Dll/ on start-up; no build step needed.
To update: replace the zip and delete the files starting with NebulaResume in Cache/Dll/.
```

To package from source, zip the contents of `src/` (`addon.meta`, `Scripts/`, `Language/`, …) with **the files at the root of the zip**, and name it `NebulaResume.zip`.

#### Server side

In Fmpostor's `config.json`:

```json
"Reconnect": {
  "Enabled": true,
  "GracePeriodSeconds": 45,
  "EnableSpectator": false,
  "MaxSpectators": 3
}
```

#### Getting back after a drop

1. The game shows its usual disconnect popup; the add-on adds a line saying you can rejoin this round on the same server. Note the **room code** first (it is always shown in the lobby).
2. Back to the main menu → same server → "Enter code" → join.
3. Role, position, tasks and alive-or-dead state are restored, and the results screen shows "Reconnected" instead of "Disconnected".

Notes:

- **You cannot reconnect while a meeting is running.** The attempt is refused **without** using your reserved slot; just join again after the meeting. Failed attempts do not count against the reconnect limit.
- The slot is only kept for a limited time (default 45 s, set by the server operator).
- Kicks and bans never keep a slot.

#### Room settings (mod settings panel, "Reconnect" group)

| Setting | Default | Meaning |
|---|---|---|
| Stay alive after reconnect | On | When off, a player who dropped while alive comes back as a ghost |
| Max reconnects per round | 3 | 0–10; beyond this the reconnect is refused until the round ends |
| Allow spectators mid-round | Off | When on, new players may join a running round as spectators |
| Spectator limit | 2 | Spectators share the same 32 player slots |

Fixed rules:

```
Dead before the drop           →  comes back as a ghost
Brand-new player mid-round     →  neutral spectator role + ghost; no team, never affects the result
```

#### Troubleshooting

Logs are in `<game folder>/BepInEx/LogOutput.log`, every line prefixed with `[NebulaResume]`. The key ones:

| Log line | Meaning |
|---|---|
| `恢复完成 服务端playerId=…` | The state snapshot was applied |
| `职业同步: 已向房主请求全场职业` → `职业同步: playerId=… 职业Id=…` | The reconnecter received the other players' real roles (the host logs `已把 N 名玩家的职业发给 …`) |
| `状态包丢弃: 会议进行中 → 本次重连已取消` | Refused because of a meeting; join again afterwards (a popup also says so in game) |
| `失效玩家清扫: playerId=…` | Leftovers of the old identity were cleaned up |

---

### 2. How it works

#### Division of labour: the server is a container; mod state is only interpreted on clients

NOS keeps roles, modifiers and so on **separately on every client**; the server only sees the vanilla role enum. So:

- **Client (this add-on)**: encodes every player's NOS state as an `int[]` and reports it periodically; on reconnect it restores the snapshot locally.
- **Server (Fmpostor)**: stores and forwards snapshots as **opaque bytes** without interpreting them, and handles slot reservation, recognising returning players, the meeting gate and telling the host to spawn a body.

As a result, a NOS update needs no server change, only an add-on update.

#### A reconnect, step by step

```
 ① In game     every client reports the state it sees once per second (kind=1);
               the server caches the latest copy per player
 ② Drop        the server keeps the player's slot (GracePeriodSeconds); the snapshot is frozen
 ③ Rejoin      the server recognises the identity (friend code / PUID) and lets the player
               into the running round; during a meeting it refuses without touching the slot
 ④ Body        the server sends the host a spawn directive (kind=4); the host client spawns a
               new body (new PlayerId). The server never creates or serialises a character
 ⑤ Clean-up    the server removes the old identity and despawns the old body
 ⑥ Resume      once the body exists, the server sends the resume signal (kind=3) + snapshot (kind=2)
 ⑦ Restore     the add-on restores role (broadcast to everyone with its parameters), ghost role,
               modifiers, alive/dead, tasks and position, then asks the host for everyone's
               roles (kind=11/12)
 ⑧ Confirm     the slot is only spent after the connection stays up for ResumeConfirmSeconds;
               dropping before that hands the slot back
```

#### Protocol

Uses NOS's own RPC channel (vanilla `RpcCalls = 128`), so no extra patching is needed. Frame layout:

```
[processHash:int32][count:int32][count × int32]
```

The first int of a frame is its kind:

| kind | Direction | Payload | Meaning |
|---|---|---|---|
| 1 | client → server | `[1, playerId, …state]` | Report a state snapshot |
| 2 | server → client | `[2, playerId, …state]` | Deliver a state snapshot |
| 3 | server → client | `[3, playerId]` | Resume signal |
| 4 | server → host | `[4, clientId, playerId]` | Spawn a body (`-1` = use a new free id) |
| 5 | client → server | `[5, playerId]` | "I am dead" (re-sent periodically) |
| 6 | server → all | `[6, N, clientId…]` | Dead roster (full) |
| 7 | server → client | `[7, 0/1]` | Spectator verdict |
| 8 | server → all | `[8, N, clientId…]` | Spectator list (full) |
| 9 | host → server | `[9, version, flags, spectatorLimit]` | Room capability declaration (bit0 reconnect, bit1 spectator) |
| 10 | host → all | `[10, inMeeting, meetingTimerMs, emergencyCooldownMs]` | Meeting state: opens/closes the server's meeting gate; mid-joiners align the emergency button timer with it |
| 11 | reconnecter → host | `[11, requesterPlayerId]` | Request everyone's roles |
| 12 | host → reconnecter | `[12, targetPlayerId, subjectPlayerId, …state]` | Role reply, one player per frame |

State snapshot (v3):

```
[0]  version = 3
     main role id / arg count / args…
     ghost role id / arg count / args…
     modifier count / per modifier (id / arg count / args…)
     dead (0/1)
     has position (0/1), X, Y (coordinates ×1000 as integers)
     task count N, N × [taskId][typeId][completed]
```

The server never parses the snapshot and never assumes a fixed length.

#### The hard parts

- **New identity instead of the old one**: a vanilla client cannot hand an existing body over to a new connection, so the returning player gets a new PlayerId and the server cleans up the old identity. State is restored per person, not per PlayerId.
- **Mid-joiners don't know other players' roles**: NOS assigns roles once, at game start. The reconnecter first gives everyone a placeholder role (to avoid null references), then asks the host for the real roles and applies them locally only (`RoleSync`).
- **The reconnecter's own role** is broadcast to everyone with its parameters; otherwise others see a blank crewmate and the win check can go wrong.
- **Meetings**: joining during a meeting always fails, because the meeting UI syncs before the body does. So the server refuses during meetings without touching the slot. The host broadcasts the meeting state every second (kind=10), and the server uses it to know when the meeting is really over, because NOS cancels the vanilla voting-complete message.
- **Position** is sampled from the player's **own body** and frozen to the pre-meeting value during the meeting, exile animation and respawn pick, so nobody comes back standing at the meeting table.
- **Post-meeting sync**: a mid-joiner never saw the game start, so it lacks the cosmetics cache (`CosmeticsWarmup`), the emergency meeting timers (`EmergencySync`) and the acknowledgements the post-meeting flow waits for (`SyncAssist`). All of these are filled in.
- **Leftover objects**: after the old body is despawned, NOS may still treat that player as present and read a destroyed object's position every frame, causing a native-level exception. `StalePlayerSweep` marks such players as disconnected.

#### Source layout

```
src/
  addon.meta              add-on metadata
  CustomServer.json       preset server
  Language/               Chinese / English text
  Scripts/
    Entry.cs              entry point, heartbeat, main restore flow
    NebulaStateWire.cs    RPC send/receive and frame dispatch
    NebulaStateCodec.cs   state capture / encoding / restore
    ResumeTransition.cs   scene transition and local rebuild on resume
    HostBodySpawner.cs    host side: spawn the reconnecter's body on request
    RoleSync.cs           reconnecter syncs everyone's roles from the host (kind=11/12)
    SpawnGuard.cs         register mid-round players into NOS immediately, with a placeholder role
    StalePlayerSweep.cs   clean up players whose body was destroyed
    MeetingFlag.cs        host broadcasts meeting state (kind=10)
    EmergencySync.cs      align emergency meeting timers
    SyncAssist.cs         supply the acknowledgements the post-meeting flow needs
    CosmeticsWarmup.cs    fill the cosmetics cache (meeting vote areas)
    DeadRoster.cs         dead roster (kind=5/6)
    SpectatorSeat.cs / SpectatorRole.cs   spectator seats and the neutral spectator role
    RoomCapability.cs     room capability declaration (kind=9)
    ReconnectPrompt.cs    disconnect / refusal prompts
    ReconnectStateTag.cs  "Reconnected" on the results screen
    FrameGuard.cs         frame-level exception guard during the reconnect window
    ResumeOptions.cs      settings
    …
NebulaResume.zip          ready-made package for Addons
```

### Known limitations

- If the host drops, the host role cannot be handed back (a new host must take over); a solo room cannot be recovered.
- Compatibility depends on NOS's internal APIs; a major NOS update may need an add-on update.
- State is reported once per second, so a role change in the last second before a drop may be lost.

### Disclaimer

This is a standalone add-on. It does not contain, modify or redistribute the NOS mod itself. Among Us and related assets are © Innersloth LLC.

### License

GNU General Public License v3.0 — see [LICENSE](LICENSE).
