// ============================================================
// HostBodySpawner.cs — 房主端补生成身体
//
// ▸ 解决什么问题
//   重连的玩家回到对局时, 服务端那边没有他的角色对象(掉线时被释放了),
//   而香草的 CreatePlayer 不会为"中途加入已开始对局"的连接造身体 ——
//   结果是服务端等 player.Character 超时, 把玩家踢掉(日志表现为 spawn timed out)。
//
// ▸ 为什么必须由房主做
//   服务端的 InnerPlayerControl.SerializeAsync 是【故意未实现】的(throw
//   NotImplementedException), 所以服务端任何"自己把角色发出去"的尝试都会抛异常
//   这是此前崩溃的直接原因。原版里角色对象的生成消息【只由房主客户端发出】。
//
// ▸ 我们怎么知道该给谁造
//   另一种做法是让服务端下发一条指令("给 ClientId=N 造身体")。
//   本实现不需要那条指令 —— 房主自己就能看出来: 【对局进行中, 有一条连接没有身体】。
//   这样服务端一行都不用改。
//
// ▸ 三道守卫(缺一不可 —— 这是本模块崩溃防线的核心)
//     ① cd.Character != null  → 已有身体, 什么都不做
//     ② cd.IsBeingCreated     → 香草正在造, 【绝对不能插手】
//     ③ !cd.InScene           → 人还没进场景, 造出来是半初始化的
//
//   ⚠️ 第 ② 条是最关键的一条。
//     香草的 CreatePlayer 在协程开头把 IsBeingCreated 置真、造完才置假,
//     中间要等这名玩家 InScene(上限 10 秒)。这整段时间里 Character 一直是 null ——
//     只看 Character 就会误判成"香草没造", 于是并发开出第二具身体,
//     两个协程抢同一个 GetAvailableId() 和同一条 GameData 记录,
//     结果是半初始化的 PlayerInfo 被下发, 之后任何一次序列化都会在原生层解引用空指针。
// ============================================================

namespace NebulaResume;

internal static class HostBodySpawner
{
    /// <summary>先让给香草的宽限期(毫秒)。这段时间只观察, 不动手。</summary>
    private const long VanillaGraceMs = 3000;

    /// <summary>补生成的尝试窗口(毫秒)。超时就不再重试。</summary>
    private const long SpawnRetryWindowMs = 15000;

    /// <summary>发起生成后, 等身体挂上的确认时间(毫秒)。</summary>
    private const long SpawnConfirmMs = 8000;

    /// <summary>已处理过的 ClientId(成功造好 或 判定不该造)。</summary>
    private static readonly System.Collections.Generic.HashSet<int> _handled =
        new System.Collections.Generic.HashSet<int>();

    /// <summary>ClientId → 重试截止时刻(Unity 时间)。</summary>
    private static readonly System.Collections.Generic.Dictionary<int, float> _retryUntil =
        new System.Collections.Generic.Dictionary<int, float>();

    /// <summary>ClientId → 宽限期截止时刻。宽限期内只观察。</summary>
    private static readonly System.Collections.Generic.Dictionary<int, float> _graceUntil =
        new System.Collections.Generic.Dictionary<int, float>();

    /// <summary>当前正在生成中的 ClientId(-1 = 没有)。同一时刻只允许一具。</summary>
    private static int _spawningClientId = -1;

    /// <summary>正在生成的那一具的确认截止时刻。</summary>
    private static float _spawningDeadline;

    private static bool _amHostWarned;
    private static readonly System.Collections.Generic.HashSet<int> _blockedLogged =
        new System.Collections.Generic.HashSet<int>();
    private static float _envLogAt;

    /// <summary>ClientId → 下一次 InScene 诊断的时刻(每 2 秒一条)。</summary>
    private static readonly System.Collections.Generic.Dictionary<int, float> _inSceneLogAt =
        new System.Collections.Generic.Dictionary<int, float>();

    /// <summary>ClientId → 下一次 IsBeingCreated 诊断的时刻(每 2 秒一条)。</summary>
    private static readonly System.Collections.Generic.Dictionary<int, float> _ibcLogAt =
        new System.Collections.Generic.Dictionary<int, float>();

    /// <summary>回主菜单/新对局时复位。</summary>
    internal static void Reset()
    {
        _handled.Clear();
        _retryUntil.Clear();
        _graceUntil.Clear();
        _blockedLogged.Clear();
        _inSceneLogAt.Clear();
        _ibcLogAt.Clear();
        _spawningClientId = -1;
        _spawningDeadline = 0f;
        _amHostWarned = false;
        _directives.Clear();
        _mustDead.Clear();
        _assertDeadAt = 0f;
        _deadMissingLogged.Clear();
    }

    // ══════════════════════════════════════════════════════════
    // 服务端补生成指令(精确还原保槽里的原 PlayerId)
    // ══════════════════════════════════════════════════════════
    //
    // ▸ 为什么需要服务端指令
    //   香草的 CreatePlayer 会用 GameData.GetAvailableId() 取一个【空闲】id ——
    //   那等于把重连者当成新玩家, 不是"恢复原身份"。而房主手里没有任何信息能知道
    //   "这条新连接对应保槽里的哪个 PlayerId"。只有服务端知道, 所以由它下发。
    //
    // ▸ 生成动作照香草同一配方(见其 CreatePlayer):
    //     Instantiate(PlayerPrefab) → 赋 PlayerId → Spawn(IsClientCharacter)
    //   生成消息由房主发出、服务端转发; 各客户端收到后自行把 InScene 置真并挂上
    //   Character(香草客户端的 SpawnCharacter 处理路径), 不需要我们再模拟。

    private sealed class SpawnDirective
    {
        public int ClientId;
        public int PlayerId;
        public float ReceivedAt;

        /// <summary>是否已经请香草 CreatePlayer 尝试过一次。</summary>
        public bool VanillaTried;
    }

    private static readonly System.Collections.Generic.Dictionary<int, SpawnDirective> _directives =
        new System.Collections.Generic.Dictionary<int, SpawnDirective>();

    private static float _directiveEnvLogAt;

    /// <summary>
    ///     本局必须显示为【死亡】的 clientId(重连者)。整局常驻, 定期对账。
    ///
    ///   ⚠️ 关键机制(源码结论): 香草层 NetworkedPlayerInfo.IsDead 是服务端所有的数据,
    ///      由【房主客户端】负责序列化下发。非房主改自己的 Data.IsDead 只改本进程,
    ///      下一轮房主序列化就会覆盖回去 —— 别人眼里仍是活人。
    ///      所以房主必须把它写成 true 并 MarkDirty(), 全房才会都看到"这个人死了"。
    ///      会议里按死者显示、死者之间互相看得见、死者聊天, 全都依赖这一条权威数据。
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<int> _mustDead =
        new System.Collections.Generic.HashSet<int>();

    private static float _assertDeadAt;

    /// <summary>已经报过"找不到身体"的 clientId, 避免每轮刷屏。</summary>
    private static readonly System.Collections.Generic.HashSet<int> _deadMissingLogged =
        new System.Collections.Generic.HashSet<int>();

    /// <summary>等待被踢出的连接(会议/放逐期间接入的人)。收包回调里只入队, 执行放到 Tick。</summary>
    private static readonly System.Collections.Generic.List<int> _kickQueue =
        new System.Collections.Generic.List<int>();

    /// <summary>每个待踢连接的执行时刻。</summary>
    private static readonly System.Collections.Generic.Dictionary<int, float> _kickDueAt =
        new System.Collections.Generic.Dictionary<int, float>();

    /// <summary>
    ///     会议中接入的连接, 先给它这么多秒自己离开, 到点还在才踢(秒)。
    ///
    ///   ▸ 为什么要等
    ///     装了本插件的客户端发现"会议中、自己还没有身体"会立刻自行退出。
    ///     自行退出在服务端看来是普通离开, 服务端会把它刚用掉的保槽原样归还,
    ///     散会后再进还能恢复职业; 而被房主踢出属于"踢出", 保槽不会归还。
    ///     不会自行离开的客户端(原版 / 旧版本)到点照旧踢出, 行为与以前相同。
    /// </summary>
    private const float KickGraceSeconds = 6f;

    /// <summary>收到服务端的补生成指令。只入队, 真正动手放到 Tick。</summary>
    internal static void OnServerDirective(int clientId, int playerId)
    {
        try
        {
            FrameGuard.Arm();     // 房主端同样进入保护期(新身体即将生成)

            // ── 会议 / 放逐进行中接入的连接: 直接踢掉, 不生成身体 ──
            //
            //   这时往对局里塞一具身体只会得到半初始化的会议界面(投票格按会议开始
            //   那一刻的玩家定长搭建, 中途插人必炸), 而且被插进来的人自己也会卡在
            //   数据同步上, 反过来把全房拖住。所以最干净的做法是: 房主立刻把他请出去,
            //   让他等散会再重新进服 —— 连提示词都不需要。
            //
            //   ⚠️ 这里【只入队】: KickPlayer 会发包, 而本方法运行在收包回调里,
            //      直接调用等于在网络层读消息的中途重入网络层。真正执行在 Tick。
            //   ★ 开了"允许会议中重连"时例外: 服务端只会在会议中给【观战者】发这条指令
            //     (重连者的续局推迟到散会), 观战者要一具身体才能和死者聊天 →
            //     照常入队, 会议中就生成(放逐动画期间先等着, 动画结束再生成)。
            if ((MeetingHud.Instance != null || ExileController.Instance != null) && !MeetingSpawnAllowed())
            {
                if (!_kickQueue.Contains(clientId))
                {
                    _kickQueue.Add(clientId);
                    _kickDueAt[clientId] = UnityEngine.Time.unscaledTime + KickGraceSeconds;
                }

                NebulaResumeEntry.Log("补生成指令(会议中): clientId=" + clientId
                    + " 原playerId=" + playerId + " → 不生成身体, " + (int)KickGraceSeconds
                    + " 秒内未自行离开则踢出。");
                return;
            }

            // ⚠️ 不能在这里无条件把重连者登记为死者。
            //   重连者可能是【活人】回来(掉线前活着), 服务端指令里没有生死标志,
            //   无条件登记会造成"别人看他是鬼、他自己是活人"的分裂
            //   这是该问题的直接表现。
            //   真正的生死由重连者自己按 kind=5 上报, 服务端再广播名册。
            _directives[clientId] = new SpawnDirective
            {
                ClientId = clientId,
                PlayerId = playerId,
                ReceivedAt = UnityEngine.Time.unscaledTime,
            };

            NebulaResumeEntry.Log("补生成指令已入队: clientId=" + clientId + " 原playerId=" + playerId);
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("补生成指令入队异常: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>
    ///     房主: 把会议期间接入的连接踢出房间。在 Tick 的干净调用栈上执行。
    /// </summary>
    private static void DrainKickQueue(AmongUsClient client)
    {
        if (_kickQueue.Count == 0)
        {
            return;
        }

        // 只处理到点的; 没到点的留在队列里, 给它时间自行离开(见 KickGraceSeconds)。
        var now = UnityEngine.Time.unscaledTime;
        var list = new System.Collections.Generic.List<int>();

        for (var q = _kickQueue.Count - 1; q >= 0; q--)
        {
            var id = _kickQueue[q];
            if (_kickDueAt.TryGetValue(id, out var due) && now < due)
            {
                continue;
            }

            list.Add(id);
            _kickQueue.RemoveAt(q);
            _kickDueAt.Remove(id);
        }

        for (var i = 0; i < list.Count; i++)
        {
            var clientId = list[i];

            try
            {
                InnerNet.ClientData? cd = null;
                var clients = client.allClients;

                if (clients != null)
                {
                    for (var k = 0; k < clients.Count; k++)
                    {
                        var c = clients[k];
                        if (c != null && c.Id == clientId)
                        {
                            cd = c;
                            break;
                        }
                    }
                }

                if (cd == null)
                {
                    NebulaResumeEntry.Log("会议中接入的连接已不在房间, 无需踢出: clientId=" + clientId);
                    continue;
                }

                string name;
                try { name = cd.PlayerName ?? "?"; } catch { name = "?"; }

                client.KickPlayer(clientId, false);
                NebulaResumeEntry.Log("会议中接入的连接已踢出: clientId=" + clientId
                    + " 名字=\"" + name + "\"(会议期间不生成身体)。");
            }
            catch (System.Exception e)
            {
                NebulaResumeEntry.Log("踢出会议中接入的连接失败 clientId=" + clientId
                    + ": " + e.GetType().Name + " : " + e.Message);
            }
        }
    }

    private static void ProcessDirectives(AmongUsClient client)
    {
        if (_directives.Count == 0)
        {
            return;
        }

        var clients = client.allClients;
        if (clients == null)
        {
            return;
        }

        System.Collections.Generic.List<SpawnDirective> snapshot = null;
        foreach (var kv in _directives)
        {
            (snapshot ??= new System.Collections.Generic.List<SpawnDirective>()).Add(kv.Value);
        }

        if (snapshot == null)
        {
            return;
        }

        for (var i = 0; i < snapshot.Count; i++)
        {
            var dir = snapshot[i];
            var now = UnityEngine.Time.unscaledTime;

            InnerNet.ClientData cd = null;
            for (var k = 0; k < clients.Count; k++)
            {
                var c = clients[k];
                if (c != null && c.Id == dir.ClientId)
                {
                    cd = c;
                    break;
                }
            }

            if (cd == null)
            {
                if (now - dir.ReceivedAt > 10f)
                {
                    _directives.Remove(dir.ClientId);
                    NebulaResumeEntry.Log("补生成指令: clientId=" + dir.ClientId + " 已不在房间, 丢弃。");
                }

                continue;
            }

            if (cd.Character != null)
            {
                // 香草已完成生成。
                //
                // ⚠️ 不能在这里登记为"必须死亡"。新规则是【按原状恢复】:
                //   掉线前活着 → 回来是活人; 掉线前已死 → 回来是灵魂。
                //   生死由【重连者自己】按状态包决定, 并通过 kind=5 上报,
                //   服务端再广播名册。房主不能擅自替他决定。
                _directives.Remove(dir.ClientId);
                NebulaResumeEntry.Log("补生成指令: clientId=" + dir.ClientId
                    + " 已有身体(香草已完成)。");
                continue;
            }

            if (_handled.Contains(dir.ClientId))
            {
                _directives.Remove(dir.ClientId);
                continue;
            }

            if (!HostSpawnEnvironmentReady(client, MeetingSpawnAllowed()))
            {
                if (now >= _directiveEnvLogAt)
                {
                    _directiveEnvLogAt = now + 2f;
                    NebulaResumeEntry.Log("补生成指令: 等待环境 —— 大厅=" + (LobbyBehaviour.Instance != null)
                        + " GameState=" + client.GameState
                        + " 船=" + (ShipStatus.Instance != null)
                        + " GameData=" + (GameData.Instance != null)
                        + " 会议=" + (MeetingHud.Instance != null)
                        + " 放逐=" + (ExileController.Instance != null));
                }

                continue;
            }

            // ── 先请香草 CreatePlayer 生成(与它一致: 资料/名字/颜色由香草配好) ──
            //   香草会自己等 InScene; 若它卡住, 6 秒后由本插件兜底生成。
            if (!dir.VanillaTried)
            {
                dir.VanillaTried = true;

                try
                {
                    string nm0;
                    try { nm0 = cd.PlayerName ?? "?"; } catch { nm0 = "?"; }

                    NebulaResumeEntry.Log("补生成: 请香草 CreatePlayer 生成 clientId=" + dir.ClientId
                        + " 名字=" + nm0 + "。");

                    client.StartCoroutine(client.CreatePlayer(cd));
                }
                catch (System.Exception ve)
                {
                    NebulaResumeEntry.Log("补生成: 香草 CreatePlayer 启动失败: "
                        + ve.GetType().Name + " : " + ve.Message);
                }

                continue;
            }

            // 香草宽限 6 秒(香草 CreatePlayer 是个协程, 抢太早可能撞出两个身体)
            if (now - dir.ReceivedAt < 6f)
            {
                continue;
            }

            // 香草仍未生成 → 手搓兜底(资料字段会在 ForceSpawn 里补齐)
            if (ForceSpawn(client, cd, dir.PlayerId))
            {
                _handled.Add(dir.ClientId);

                // 不登记死亡名册 —— 由重连者按原状自行上报。

                _directives.Remove(dir.ClientId);
                return;   // 一帧只造一具, 其余下一帧继续
            }
        }
    }

    /// <summary>
    ///     房主端: 把"重连者必须是死者"的权威生死态持续写回并广播。
    ///
    ///   为什么必须房主写: NetworkedPlayerInfo.IsDead 由房主序列化下发, 非房主
    ///   改自己的那份不会传播; 而会议按死者显示、死者互相可见、死者聊天都依赖它。
    ///   MarkDirty() 是必要条件 —— 不标脏就不会被序列化出去。
    ///
    ///   每 250ms 一轮、整局常驻: PlayerInfo 被重建 / OnGameStart 复位等都可能把它
    ///   改回 false, 一次性写入不够。
    /// </summary>
    private static void AssertMustDead(AmongUsClient client)
    {
        // 只处理【观战者】(全新玩家中途加入)。
        // 重连者的生死由他自己按原状决定并上报, 房主不能代劳。
        if (_mustDead.Count == 0)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _assertDeadAt)
        {
            return;
        }

        _assertDeadAt = now + 0.25f;

        if (!client.AmHost)
        {
            return;
        }

        var all = PlayerControl.AllPlayerControls;
        if (all == null)
        {
            return;
        }

        // 命中方式: 按 OwnerId(生成时指定的归属)或资料里的 ClientId。
        //   ⚠️ 新生成的身体, 资料里的 ClientId 可能还是 0(没被赋值),
        //      所以【必须】同时按 OwnerId 匹配 —— 只用 Data.ClientId 会永远找不到人,
        //      表现就是"重连人自己看自己是鬼, 别人看他还是活人, 甚至能击杀他"。
        var matched = new System.Collections.Generic.HashSet<int>();

        for (var i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (pc == null || pc.Data == null)
            {
                continue;
            }

            var ownerId = -1;
            try { ownerId = pc.OwnerId; } catch { }

            var dataClient = -1;
            try { dataClient = pc.Data.ClientId; } catch { }

            var hit = -1;
            if (_mustDead.Contains(ownerId))
            {
                hit = ownerId;
            }
            else if (_mustDead.Contains(dataClient))
            {
                hit = dataClient;
            }

            if (hit < 0)
            {
                continue;
            }

            matched.Add(hit);

            try
            {
                // 资料里的 ClientId 对齐到真实归属(新身体常是 0)
                if (dataClient != hit)
                {
                    pc.Data.ClientId = hit;
                }

                if (!pc.Data.IsDead)
                {
                    pc.Data.IsDead = true;
                    pc.Data.MarkDirty();
                    NebulaResumeEntry.Log("权威生死态: clientId=" + hit
                        + " 已置死亡并广播(owner=" + ownerId + " dataClient=" + dataClient + ")。");
                }
            }
            catch (System.Exception e)
            {
                NebulaResumeEntry.Log("权威生死态写入失败 clientId=" + hit + ": " + e.Message);
            }
        }

        // 诊断: 名册里的人在场上找不到对应身体时, 只报一次。
        foreach (var want in _mustDead)
        {
            if (matched.Contains(want))
            {
                continue;
            }

            if (_deadMissingLogged.Add(want))
            {
                NebulaResumeEntry.Log("权威生死态: 暂时找不到 clientId=" + want
                    + " 的身体(等它生成); 全场可见性会在找到后立即对齐。");
            }
        }
    }

    /// <summary>
    ///     按香草的配方为指定连接生成角色, 但 PlayerId 用服务端保槽里的原值。
    /// </summary>
    private static bool ForceSpawn(AmongUsClient client, InnerNet.ClientData cd, int playerId)
    {
        try
        {
            var gd = GameData.Instance;
            var ship = ShipStatus.Instance;

            if (gd == null || ship == null || client.PlayerPrefab == null)
            {
                return false;
            }

            var center = ship.InitialSpawnCenter;
            var pos = new UnityEngine.Vector3(center.x, center.y, 0f);

            // playerId < 0 = 服务端要求"按普通加入处理", 用空闲 id 新建身份。
            // 这正是香草 CreatePlayer 的做法, 也是避开"旧 PlayerId 留下陈旧引用"
            // 的关键: 旧身体已销毁, 若复用同一个 PlayerId, 所有客户端的 NoS
            // PlayerModInfo 仍指向已销毁的旧对象 → 每帧 get_transform() 空引用
            // → 其他人陆续掉线、会议 UI 崩。
            if (playerId < 0)
            {
                var freeId = gd.GetAvailableId();
                if (freeId < 0)
                {
                    NebulaResumeEntry.Log("补生成: 没有空闲 PlayerId, 稍后重试。");
                    return false;
                }

                playerId = freeId;
            }

            var pc = UnityEngine.Object.Instantiate(
                client.PlayerPrefab, pos, UnityEngine.Quaternion.identity);

            if (pc == null)
            {
                NebulaResumeEntry.Log("补生成: Instantiate 失败。");
                return false;
            }

            pc.PlayerId = (byte)playerId;

            // ★ 与香草 CreatePlayer 对齐: 资料字段必须一起配好, 否则
            //   · Data.ClientId = 0  → 我们按 clientId 找不到这个人(死亡态写不上);
            //   · Data.PlayerName 空 → 别人/会议里看到的是没有名字的默认小人。
            try
            {
                if (pc.Data != null)
                {
                    pc.Data.ClientId = cd.Id;

                    string nm1;
                    try { nm1 = cd.PlayerName ?? string.Empty; } catch { nm1 = string.Empty; }

                    if (!string.IsNullOrEmpty(nm1))
                    {
                        pc.Data.PlayerName = nm1;
                    }
                }
            }
            catch (System.Exception ie)
            {
                NebulaResumeEntry.Log("补生成: 资料字段写入失败: " + ie.Message);
            }

            // 生成消息由房主发出, 服务端转发给所有人(含重连者本人)
            client.Spawn(pc, cd.Id, InnerNet.SpawnFlags.IsClientCharacter);

            cd.Character = pc;

            // 让新控制与玩家资料建立登记关系:
            //   · 资料已在(保槽保留的原 PlayerId) → 不动它, 香草的接收路径会把
            //     clientData.Character / InScene 挂好, 控制自身在 Start 时按 PlayerId 找资料;
            //   · 资料缺失 → 用控制自带的 Data 补登记, 否则各客户端收到针对这具身体
            //     的 RPC(改名/变色/位置)时会因资料为空而每帧空引用。
            var existing = gd.GetPlayerById((byte)playerId);
            if (existing == null)
            {
                if (pc.Data != null)
                {
                    gd.AddPlayerInfo(pc.Data);
                    NebulaResumeEntry.Log("补生成: 已为 playerId=" + playerId + " 登记玩家资料。");
                }
                else
                {
                    NebulaResumeEntry.Log("补生成提醒: playerId=" + playerId + " 新身份但控制无 Data。");
                }
            }

            NebulaResumeEntry.Log("补生成完成(香草配方): clientId=" + cd.Id
                + " 角色 playerId=" + playerId + " netId=" + pc.NetId);
            // 全房同步回执由重连者自己补交(见 SyncAssist), 房主替别人发是无效的。

            return true;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("补生成异常: " + e.GetType().Name + " : " + e.Message);
            return false;
        }
    }

    /// <summary>由 Entry.Tick 每帧调用。</summary>
    internal static void Tick()
    {
        try
        {
            var client = AmongUsClient.Instance;
            if (client == null)
            {
                return;
            }

            // 回到主菜单 → 清空, 避免跨局残留
            if (LobbyBehaviour.Instance == null && ShipStatus.Instance == null)
            {
                if (_handled.Count > 0 || _spawningClientId >= 0)
                {
                    Reset();
                    NebulaResumeEntry.Log("补生成: 已离开对局, 状态复位。");
                }

                return;
            }

            // 只有房主能造身体。
            // ★ 诊断: 这个分支是【静默】的, 多开测试时全场可能没有一条补生成日志 ——
            //   无法区分"不是房主"还是"扫描没发现目标"。所以这里每局报一次本机的身份状态。
            if (!client.AmHost)
            {
                if (!_amHostWarned)
                {
                    _amHostWarned = true;
                    NebulaResumeEntry.Log("补生成: 本机不是房主 (AmHost=false, ClientId=" + client.ClientId
                        + "), 不执行补生成。");
                }

                return;
            }

            // ── 会议期间接入的连接: 先踢出去(不生成身体) ──
            DrainKickQueue(client);

            // ── 服务端补生成指令(优先于通用扫描) ──
            ProcessDirectives(client);

            // ── 房主: 维护"重连者必须是死者"的权威生死态 ──
            AssertMustDead(client);

            // 正在生成中的那一具有没有挂上?
            if (_spawningClientId >= 0)
            {
                var alive = UnityEngine.Time.unscaledTime <= _spawningDeadline;

                if (!alive)
                {
                    // 超时 —— 记为"不再重试"(重试正是双重生成本身)
                    NebulaResumeEntry.Log("补生成: ClientId=" + _spawningClientId
                        + " 生成后迟迟没挂上身体, 放弃该次。");
                    _handled.Add(_spawningClientId);
                    _spawningClientId = -1;
                }
                else
                {
                    // 还要看它到底成没成
                    var cds = client.allClients;
                    if (cds != null)
                    {
                        for (var i = 0; i < cds.Count; i++)
                        {
                            var c = cds[i];
                            if (c == null || c.Id != _spawningClientId)
                            {
                                continue;
                            }

                            if (c.Character != null)
                            {
                                NebulaResumeEntry.Log("补生成: ClientId=" + _spawningClientId
                                    + " 身体已挂上 ✅");
                                _handled.Add(_spawningClientId);
                                _spawningClientId = -1;
                            }

                            break;
                        }
                    }

                    return;   // 同一时刻只处理一具
                }
            }

            // ── 扫描: 有没有"对局中却没有身体"的连接 ──
            if (!HostSpawnEnvironmentReady(client))
            {
                // 环境不就绪是常态(大厅/会议等), 但为了让"为什么没扫"可验证,
                // 每秒报一次当前卡在哪一条。
                var now2 = UnityEngine.Time.unscaledTime;
                if (now2 >= _envLogAt)
                {
                    _envLogAt = now2 + 1f;
                    NebulaResumeEntry.Log("补生成诊断: 环境未就绪 —— 大厅=" + (LobbyBehaviour.Instance != null)
                        + " GameState=" + client.GameState
                        + " 船=" + (ShipStatus.Instance != null)
                        + " GameData=" + (GameData.Instance != null)
                        + " 会议=" + (MeetingHud.Instance != null)
                        + " 放逐=" + (ExileController.Instance != null));
                }

                return;
            }

            var clients = client.allClients;
            if (clients == null)
            {
                return;
            }

            for (var i = 0; i < clients.Count; i++)
            {
                var cd = clients[i];
                if (cd == null)
                {
                    continue;
                }

                TrySpawnFor(client, cd);
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("补生成 Tick 异常: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>
    ///     房主端补生成的环境前置条件。任何一条不满足都【只是稍后再试】, 不是失败。
    /// </summary>
    /// <summary>开了"允许会议中重连": 服务端的补生成指令在会议中也照做(只针对观战者)。</summary>
    private static bool MeetingSpawnAllowed()
    {
        try { return ResumeOptions.MeetingJoinAllowed; } catch { return false; }
    }

    private static bool HostSpawnEnvironmentReady(AmongUsClient client) => HostSpawnEnvironmentReady(client, false);

    /// <param name="allowMeeting">
    ///     true: 会议界面开着也可以生成(服务端指令、会议中进入的观战者)。
    ///     新身体不会加进已有的会议投票格(会议只按开始时的名单搭建), 所以不影响这场会议;
    ///     放逐动画期间仍然不生成。
    /// </param>
    private static bool HostSpawnEnvironmentReady(AmongUsClient client, bool allowMeeting)
    {
        try
        {
            // 还在大厅 → 香草自己会造, 我们一步都不该插手
            if (LobbyBehaviour.Instance != null)
            {
                return false;
            }

            // 对局必须真的开着。GameState 不是 Started 时 GameData/ShipStatus
            // 可能正在建或正在拆, 这时候 Spawn 一具身体就是往半个世界里插东西。
            if (client.GameState != InnerNetClient.GameStates.Started)
            {
                return false;
            }

            if (ShipStatus.Instance == null || GameData.Instance == null)
            {
                return false;
            }

            // ⚠️ 会议 / 放逐动画期间绝不生成。
            //    MeetingHud 的 PlayerVoteArea 是按开局时的人数一次性建好的定长结构,
            //    ExileController 同理会按当时的玩家集合播动画 —— 中途插进一个新的
            //    PlayerId, 两边都会当场越界或空引用。等散会再造, 最多多等几秒。
            if (ExileController.Instance != null)
            {
                return false;
            }

            if (MeetingHud.Instance != null && !allowMeeting)
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>尝试为一个 ClientData 补生成。返回 true 表示"不用再管它了"。</summary>
    private static bool TrySpawnFor(AmongUsClient client, InnerNet.ClientData cd)
    {
        var clientId = cd.Id;

        if (_handled.Contains(clientId))
        {
            return true;
        }

        // ── 守卫 ① 已经有身体了 ──
        if (cd.Character != null)
        {
            _handled.Add(clientId);
            return true;
        }

        // ── 守卫 ② 香草正在造 —— 绝对不能插手 ──
        //   诊断每 2 秒重复: 香草的 CreatePlayer 到底是【正常进行中】还是
        //   【卡死在等 InScene】, 必须有时间线才能区分。
        if (cd.IsBeingCreated)
        {
            var now2 = UnityEngine.Time.unscaledTime;
            if (!_ibcLogAt.TryGetValue(clientId, out var nextAt) || now2 >= nextAt)
            {
                _ibcLogAt[clientId] = now2 + 2f;
                NebulaResumeEntry.Log("补生成诊断: ClientId=" + clientId
                    + " 无身体但 IsBeingCreated=true (香草 CreatePlayer 等待中, InScene=" + cd.InScene + ")。");
            }

            return false;
        }

        // ── 守卫 ③ 人还没进场景 ──
        //   诊断每 2 秒重复一次: InScene 何时翻真、翻没翻, 必须有时间线可查
        //   (早先"每客户端只打一次"的设计, 恰好把后续变化全遮住了)。
        if (!cd.InScene)
        {
            var now3 = UnityEngine.Time.unscaledTime;
            if (!_inSceneLogAt.TryGetValue(clientId, out var nextAt) || now3 >= nextAt)
            {
                _inSceneLogAt[clientId] = now3 + 2f;
                NebulaResumeEntry.Log("补生成诊断: ClientId=" + clientId
                    + " 无身体且未进场景 (InScene=false), 等待其进入场景。");
            }

            return false;
        }

        // ── 不给房主自己造 ──
        if (clientId == client.ClientId)
        {
            _handled.Add(clientId);
            return true;
        }

        var now = UnityEngine.Time.unscaledTime;

        // ── 排队 + 宽限期: 先把机会让给香草 ──
        if (!_retryUntil.ContainsKey(clientId))
        {
            _retryUntil[clientId] = now + (SpawnRetryWindowMs / 1000f);
            _graceUntil[clientId] = now + (VanillaGraceMs / 1000f);

            NebulaResumeEntry.Log("补生成: ClientId=" + clientId
                + " 无身体, 已排队(先让香草 "
                + (VanillaGraceMs / 1000f) + " 秒, 期间只观察)。");
            return false;
        }

        if (now < _graceUntil[clientId])
        {
            return false;   // 宽限期内, 继续观察
        }

        if (now > _retryUntil[clientId])
        {
            NebulaResumeEntry.Log("补生成: ClientId=" + clientId + " 尝试窗口已过, 放弃。");
            _handled.Add(clientId);
            return true;
        }

        // ── PlayerId 还有没有空位 ──
        //   GetAvailableId() 没位置时返回 -1, 强转 byte 就是 255, 之后必然查不到
        sbyte freeId;
        try
        {
            freeId = GameData.Instance.GetAvailableId();
        }
        catch
        {
            return false;
        }

        if (freeId < 0)
        {
            NebulaResumeEntry.Log("补生成: 没有可用的 PlayerId (GetAvailableId=" + freeId
                + "), 放弃 ClientId=" + clientId + "。");
            _handled.Add(clientId);
            return true;
        }

        // ── 真正动手 ──
        string nm;
        try
        {
            nm = cd.PlayerName ?? "?";
        }
        catch
        {
            nm = "?";
        }

        NebulaResumeEntry.Log("补生成: 为 ClientId=" + clientId + " 名字=\"" + nm
            + "\" 调 CreatePlayer (PlayerId 将取 " + freeId + ")。");

        _spawningClientId = clientId;
        _spawningDeadline = now + (SpawnConfirmMs / 1000f);

        try
        {
            client.StartCoroutine(client.CreatePlayer(cd));
            return true;
        }
        catch (System.Exception se)
        {
            NebulaResumeEntry.Log("补生成: CreatePlayer 启动失败 ClientId=" + clientId
                + ": " + se.GetType().Name + " : " + se.Message);
            _handled.Add(clientId);
            _spawningClientId = -1;
            return true;
        }
    }
}
