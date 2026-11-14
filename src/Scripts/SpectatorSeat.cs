// ============================================================
// SpectatorSeat.cs — 观战席
//
// ▸ 适用对象
//   【全新玩家】在对局已开始的情况下加入房间。它不是重连者 —— 没有历史状态
//   可以恢复, 所以走一套独立的处理:
//
//     重连者  → 恢复其原有职业, 生死态由状态包 kind=2 决定
//     观战者  → 一律中立观战职业 + 固定为灵魂      (本文件)
//
// ▸ 为什么不会影响胜负
//   观战者被分配的是独立中立阵营的空壳职业(见 SpectatorRole), 且始终是灵魂。
//   灵魂不计入任何一方的存活判定, 该阵营也从不声明获胜机会。
//
// ▸ 与死亡名册的关系
//   观战者同样进死亡名册(服务端 kind=6 会把它带进来), 所以"固定是灵魂"
//   这一环完全复用现有机制, 不需要第二套。
//
// ▸ 收尾时序
//   观战判定(7) → 续局过渡(3) → 身体就位 → 装观战职业 + 灵魂化。
//   每一步都可能比前一步早/晚到, 所以这里做成"条件满足就执行"的轮询,
//   而不是依赖固定顺序。
// ============================================================

namespace NebulaResume;

internal static class SpectatorSeat
{
    /// <summary>本机是否被判定为观战者。</summary>
    private static bool _amSpectator;

    /// <summary>是否已经为观战者装好职业。</summary>
    private static bool _roleAssigned;

    /// <summary>节流。</summary>
    private static float _tickAt;

    /// <summary>诊断日志节流: 收尾卡住时每 3 秒说明一次卡在哪一步。</summary>
    private static float _diagAt;
    private static string _lastDiag;

    /// <summary>没有船场景时只报一次。</summary>
    private static bool _noShipLogged;

    private static void Diag(string why)
    {
        var now = UnityEngine.Time.unscaledTime;
        if (why == _lastDiag && now < _diagAt)
        {
            return;
        }

        _diagAt = now + 3f;
        _lastDiag = why;

        NebulaResumeEntry.Log("观战席: 收尾等待中 —— " + why
            + " (过渡完成=" + ResumeTransition.Done + ")");
    }

    // ══════════════════════════════════════════════════════════
    // 观战者不得获得「幽灵职业」
    // ══════════════════════════════════════════════════════════
    //
    // ▸ 问题
    //   NOS 的死亡流程会给死者分配一个【幽灵职业】(吵闹鬼 / 幽灵灯火 / 幻影 ...)。
    //   观战者是"被强制变成灵魂"的, 不希望在观战席上带着吵闹鬼这种能影响
    //   对局的能力(它会制造假声音误导玩家)。
    //
    // ▸ 机制(源码结论)
    //   NebulaGameManager.ChangeToSpectator() 里有一句
    //       if (!LocalPlayer.AttemptedGhostAssignment) RpcTryAssignGhostRole.Invoke(LocalPlayer);
    //   RpcTryAssignGhostRole 的接收端在【房主】上执行 RoleAllocator.AssignToGhost(...),
    //   随机挑一个幽灵职业并广播下去。请求是【本人客户端】发出的, 所以只要
    //   在本机把 AttemptedGhostAssignment 置真, 请求就永远不会发出 → 不会分配。
    //
    //   ⚠️ 这个属性在接口上是 internal set, 插件不能直接赋值, 只能反射写。
    //      写法设计成幂等 + 失败不重试刷屏, 并且必须在"变鬼之前"写进去。

    private static System.Reflection.PropertyInfo? _ghostFlagProp;
    private static bool _ghostFlagMethodLooked;

    /// <summary>把"已尝试分配幽灵职业"置真, 使观战者不会拿到幽灵职业。</summary>
    private static void ArmNoGhostRole(Virial.Game.Player? nosP)
    {
        if (nosP == null)
        {
            return;
        }

        try
        {
            if (nosP.GhostRole != null)
            {
                // 已经分配过 —— 说明拦晚了。只报一次, 便于定位时序问题。
                if (_ghostRoleAlreadyLogged.Add(((IArchivedPlayer)nosP).PlayerId))
                {
                    NebulaResumeEntry.Log("观战席: playerId=" + ((IArchivedPlayer)nosP).PlayerId
                        + " 已被分配幽灵职业, 本次无法撤销(下次会在变鬼前拦住)。");
                }

                return;
            }
        }
        catch { }

        try
        {
            _ghostFlagProp ??= typeof(Virial.Game.Player).GetProperty(
                "AttemptedGhostAssignment",
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance);

            if (_ghostFlagProp == null)
            {
                return;
            }

            // 已经是 true 就不用再写。
            if (_ghostFlagProp.GetValue(nosP) is bool already && already)
            {
                return;
            }

            var setter = _ghostFlagProp.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(nosP, new object[] { true });

                if (_ghostBlockLogged.Add(((IArchivedPlayer)nosP).PlayerId))
                {
                    NebulaResumeEntry.Log("观战席: 已禁止幽灵职业分配(playerId="
                        + ((IArchivedPlayer)nosP).PlayerId + ") —— 观战者保持为普通灵魂。");
                }

                return;
            }

            // 兜底: 接口属性拿不到 setter 时, 走显式接口实现的映射表。
            if (!_ghostFlagMethodLooked)
            {
                _ghostFlagMethodLooked = true;
                NebulaResumeEntry.Log("观战席: 幽灵职业开关没有 setter, 尝试接口映射。");
            }

            var map = nosP.GetType().GetInterfaceMap(typeof(Virial.Game.Player));
            for (var i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name != "set_AttemptedGhostAssignment")
                {
                    continue;
                }

                map.TargetMethods[i].Invoke(nosP, new object[] { true });

                if (_ghostBlockLogged.Add(((IArchivedPlayer)nosP).PlayerId))
                {
                    NebulaResumeEntry.Log("观战席: 已禁止幽灵职业分配(接口映射, playerId="
                        + ((IArchivedPlayer)nosP).PlayerId + ")。");
                }

                return;
            }
        }
        catch (System.Exception e)
        {
            var key = e.GetType().Name + ":" + e.Message;
            if (_ghostBlockError != key)
            {
                _ghostBlockError = key;
                NebulaResumeEntry.Log("观战席: 禁止幽灵职业失败(同类只报一次): " + key);
            }
        }
    }

    private static readonly System.Collections.Generic.HashSet<byte> _ghostBlockLogged =
        new System.Collections.Generic.HashSet<byte>();

    private static readonly System.Collections.Generic.HashSet<byte> _ghostRoleAlreadyLogged =
        new System.Collections.Generic.HashSet<byte>();

    private static string _ghostBlockError;

    // ══════════════════════════════════════════════════════════
    // 房主: 把观战职业下发/广播给全场
    // ══════════════════════════════════════════════════════════
    //
    // ▸ 为什么必须广播, 而不是"本机装一下就行"
    //   名牌后缀(「（观战中）」)只有在【本机知道这个玩家是什么职业】时才会被画出来。
    //   职业是每客户端各持一份的视图: 用 InvokeLocal 只在本机执行, 别人的客户端里
    //   这个玩家根本没有这个职业, 连"要不要装饰"都无从判断 —— 标签就只在自己屏幕上出现。
    //
    //   走 GamePlayer.SetRole(...) 则是 NOS 自己的标准路径: 写本机 + 广播一条
    //   SetAssignable RPC, 每台客户端收到后都把这份职业登记进自己的视图。
    //   这和 NOS 给任何职业赋值走的是同一条路, 不是绕开框架的野路子。
    //
    // ▸ 为什么由房主来做
    //   房主手里同时有观战名单(kind=8)和全部身体(身体本来就是房主补生成的),
    //   由它统一发一次, 不必依赖观战者自己那份状态是否就绪。
    //
    // ▸ 为什么每 2 秒对账一次, 而不是发一次就完
    //   · 观战者的身体是房主补生成的, 别的客户端可能还没收到生成消息; 此时收到的
    //     SetAssignable 会因为找不到 PlayerControl 而空引用(NOS 的接收端没有空值保护),
    //     所以要等身体确实存在之后再发, 并在失败后继续对账;
    //   · 后加入的客户端错过了那条广播 —— 用"连接数变化"触发一次补发。
    //
    // ▸ 已经装过的人不重发
    //   按 nosP.Role.Role 判断, 已经是对应的观战职业就跳过, 避免反复换职业
    //   (换职业会重建运行时对象, 也会重复触发一次死亡态保持)。

    private static float _hostSyncAt;
    private static int _lastClientCount = -1;

    /// <summary>已经报过"找不到身体"的 clientId, 避免每轮刷屏。</summary>
    private static readonly System.Collections.Generic.HashSet<int> _syncLogged =
        new System.Collections.Generic.HashSet<int>();

    /// <summary>clientId → 房主第一次看到它身体存在的时刻。用来留一段同步余量。</summary>
    private static readonly System.Collections.Generic.Dictionary<int, float> _bodySeenAt =
        new System.Collections.Generic.Dictionary<int, float>();

    /// <summary>
    ///     看到身体之后要等多久才下发职业。
    ///
    ///   NOS 的 SetAssignable 接收端是 `RegisterPlayer(AllPlayerControls.Find(...))`,
    ///   对"本地还没有这个身体"没有任何保护(直接解引用空值)。房主这边身体挂上了,
    ///   不代表别人也收到了那条生成消息, 所以留 2 秒余量再发。
    /// </summary>
    private const float BodySyncGrace = 2f;

    internal static void TickHostRoleBroadcast()
    {
        if (_roster.Count == 0)
        {
            return;
        }

        AmongUsClient client = null;
        try { client = AmongUsClient.Instance; } catch { }

        if (client == null)
        {
            return;
        }

        // 只有房主做(且房主自己就是观战者时不用做: 那条路走本机分配)。
        var amHost = false;
        try { amHost = client.AmHost; } catch { }
        if (!amHost || _amSpectator)
        {
            return;
        }

        // 本次进房已被拒绝 / 会议进行中: 不改职业。
        if (ResumeTransition.RefusedByMeeting)
        {
            return;
        }

        // 还没进对局(大厅 / 主菜单): 没有身体可找, 也没有职业可言。
        if (ShipStatus.Instance == null)
        {
            return;
        }

        if (MeetingHud.Instance != null || ExileController.Instance != null)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;

        // 连接数变化 → 可能有新客户端错过了之前的广播 → 立刻补一轮。
        var count = -1;
        try { count = client.allClients == null ? -1 : client.allClients.Count; } catch { }

        if (count != _lastClientCount)
        {
            _lastClientCount = count;
            _hostSyncAt = 0f;
        }

        if (now < _hostSyncAt)
        {
            return;
        }

        _hostSyncAt = now + 2f;

        object? role = null;
        try { role = SpectatorRole.MyRole; } catch { }
        if (role == null)
        {
            return;
        }

        var myClientId = -1;
        try { myClientId = client.ClientId; } catch { }

        foreach (var clientId in _roster)
        {
            if (clientId == myClientId)
            {
                continue;
            }

            var pc = FindByClientId(clientId);
            if (pc == null)
            {
                if (_syncLogged.Add(clientId))
                {
                    NebulaResumeEntry.Log("观战职业: 暂时找不到 clientId=" + clientId
                        + " 的身体, 稍后继续(身体还没同步过来)。");
                }

                _bodySeenAt.Remove(clientId);
                continue;
            }

            // 看到的当帧不下发, 先给别的客户端 2 秒接收生成消息的时间。
            if (!_bodySeenAt.TryGetValue(clientId, out var seenAt))
            {
                _bodySeenAt[clientId] = now;
                continue;
            }

            if (now - seenAt < BodySyncGrace)
            {
                continue;
            }

            Virial.Game.Player? nosP = null;
            try { nosP = Virial.Game.Player.GetPlayer(pc.PlayerId); } catch { }

            if (nosP == null)
            {
                continue;   // 等生成守卫先把这名玩家注册进本机 NoS
            }

            // 已经是观战职业 → 不再重复下发。
            var already = false;
            try { already = nosP.Role != null && nosP.Role.Role == (Virial.Assignable.DefinedRole)role; }
            catch { }

            if (already)
            {
                continue;
            }

            try
            {
                // ★ 与 NOS 给任何职业赋值同一条路径: 本机写入 + 广播给所有客户端。
                nosP.SetRole((Virial.Assignable.DefinedRole)role);

                _syncLogged.Remove(clientId);
                NebulaResumeEntry.Log("观战职业: 已下发并广播给全场 clientId=" + clientId
                    + "(playerId=" + pc.PlayerId + "), 名牌标签将对所有人可见。");
                // 全房同步回执由观战者自己补交(见 SyncAssist), 房主替别人发是无效的。
            }
            catch (System.Exception se)
            {
                var inner = se is System.Reflection.TargetInvocationException tie ? tie.InnerException : se;
                NebulaResumeEntry.Log("观战职业: 广播失败 clientId=" + clientId + ": "
                    + (inner != null ? inner.GetType().Name + " : " + inner.Message : se.Message));
            }
        }
    }

    /// <summary>按 clientId 找玩家实体: 先按归属(OwnerId), 再按资料里的 ClientId。</summary>
    private static PlayerControl? FindByClientId(int clientId)
    {
        var pcs = PlayerControl.AllPlayerControls;
        if (pcs == null)
        {
            return null;
        }

        PlayerControl? byData = null;

        for (var i = 0; i < pcs.Count; i++)
        {
            var pc = pcs[i];
            if (pc == null || pc.Data == null)
            {
                continue;
            }

            try
            {
                if (pc.OwnerId == clientId)
                {
                    return pc;
                }
            }
            catch { }

            if (byData == null)
            {
                try
                {
                    if (pc.Data.ClientId == clientId)
                    {
                        byData = pc;
                    }
                }
                catch { }
            }
        }

        return byData;
    }

    /// <summary>服务端下发的观战名单(clientId)。</summary>
    private static readonly System.Collections.Generic.HashSet<int> _roster =
        new System.Collections.Generic.HashSet<int>();

    internal static bool AmSpectator => _amSpectator;

    /// <summary>本机在 NoS 层的玩家对象(还没注册时返回 null)。</summary>
    internal static Virial.Game.Player? SelfNoSPlayer()
    {
        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (lp == null)
            {
                return null;
            }

            return Virial.Game.Player.GetPlayer(lp.PlayerId);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    ///     供死亡名册在【灵魂化本机玩家之前】调用。
    ///     观战者必须先关掉幽灵职业分配, 再变鬼, 否则会拿到吵闹鬼一类的能力。
    /// </summary>
    internal static void ArmNoGhostRoleFor(Virial.Game.Player? nosP) => ArmNoGhostRole(nosP);

    internal static void Reset()
    {
        _amSpectator = false;
        _roleAssigned = false;
        _tickAt = 0f;
        _roster.Clear();
        _syncLogged.Clear();
        _bodySeenAt.Clear();
        _hostSyncAt = 0f;
        _lastClientCount = -1;
    }

    /// <summary>服务端判定本机是观战者(kind=7)。</summary>
    internal static void BeginSpectator()
    {
        if (_amSpectator)
        {
            return;
        }

        _amSpectator = true;
        NebulaResumeEntry.Log("观战席: 本机被判定为观战者, 将执行过渡并分配中立观战职业。");

        // 入场尚未完成: 这期间被移出房间要按"入场失败"提示, 而不是普通掉线。
        ReconnectPrompt.NoteAttempt("观战入场");

        // 最早时机就把"不要幽灵职业"写进去: NOS 是在死者本机死亡后一路
        // 走 ChangeToSpectator 才发出分配的, 早于那一步写就不会分配。
        try { ArmNoGhostRole(SelfNoSPlayer()); } catch { }

        // 观战者和重连者一样需要把对局环境搭起来(否则没有 HUD、动不了)。
        // ★ 会议期间新玩家【不可中途入场】: 与会议重连同样处理 —— 立刻踢回主菜单,
        //   回主菜单后按"重连失败(可能是会议进行中)"提示。不生成身体、不搭环境。
        if (MeetingFlag.Active || ExileController.Instance != null)
        {
            NebulaResumeEntry.Log("观战席: 会议进行中 → 不允许中途入场, 立即移出房间。");
            ResumeTransition.RefuseByMeeting("观战者-会议中");
            return;
        }

        try
        {
            ResumeTransition.Run(true);
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("观战席: 过渡异常: " + e.Message);
        }

        // 立刻进死亡名册的本地视图: 观战者必须是灵魂。
        // (服务端也会通过 kind=6 带来, 这里先本地登记, 不等它。)
        try
        {
            // 观战者【无条件】固定为灵魂: 既要本地登记, 也要让服务端把名册
            // 广播给全场(否则别人眼里它还是活人)。
            DeadRoster.MarkSelfSpectator();
            DeadRoster.RequireSelfAsGhost("观战者");
        }
        catch { }
    }

    /// <summary>收到服务端观战名单(kind=8)。</summary>
    internal static void ApplyServerRoster(int[] frame)
    {
        if (frame == null || frame.Length < 2)
        {
            return;
        }

        var count = frame[1];
        if (count < 0 || count > 64 || frame.Length < 2 + count)
        {
            return;
        }

        _roster.Clear();
        for (var i = 0; i < count; i++)
        {
            if (frame[2 + i] >= 0)
            {
                _roster.Add(frame[2 + i]);
            }
        }

        NebulaResumeEntry.Log("观战名单: 当前 " + _roster.Count + " 名观战者。");

        // ── 兜底: 如果名单里有自己, 但还没被 kind=7 告知 ──
        //
        //   为什么会这样: 服务端发 kind=7 时需要一个"本客户端上真实存在的角色"
        //   作为载体。观战者刚进来时还没有身体(要等房主补生成), 那时找不到载体,
        //   kind=7 就发不出来。
        //   观战名单是全量广播的, 不依赖载体, 所以用它做兜底判定。
        try
        {
            if (!_amSpectator)
            {
                var myClientId = AmongUsClient.Instance != null
                    ? AmongUsClient.Instance.ClientId
                    : -1;

                if (myClientId >= 0 && _roster.Contains(myClientId))
                {
                    NebulaResumeEntry.Log("观战席: 名单里包含本机(clientId=" + myClientId
                        + "), 但没有收到观战判定 → 自行进入观战流程。");

                    BeginSpectator();
                }
            }
        }
        catch { }
    }

    /// <summary>由 Entry.Tick 每帧调用: 条件满足后为观战者收尾。</summary>
    internal static void Tick()
    {
        // 观战者一旦被确认, 每轮都保证"不会拿到幽灵职业"先把开关写进去。
        //   这一步必须【早于】任何灵魂化动作: NOS 是在本月死亡后立刻走
        //   ChangeToSpectator 去要幽灵职业的, 晚一帧就可能已经被分到吵闹鬼。
        if (_amSpectator)
        {
            try { ArmNoGhostRole(SelfNoSPlayer()); } catch { }
        }

        if (!_amSpectator || _roleAssigned)
        {
            return;
        }
        // 本次进房已被会议拒绝(观战者会议期间同样不可入场) → 退出流程, 不再尝试搭环境。
        if (ResumeTransition.RefusedByMeeting)
        {
            return;
        }

        var now = UnityEngine.Time.unscaledTime;
        if (now < _tickAt)
        {
            return;
        }

        _tickAt = now + 0.25f;

        // 条件: 过渡完成 + 身体就位 + 本机已注册进 NoS
        //
        //   过渡没做过时自己补一次 —— 观战者有可能是通过观战名单兜底进入的,
        //   那条路径上没有 kind=3 续局信号, 过渡不会自动执行。
        //
        //   ⚠️ 但【不能】因为过渡没成功就不装职业:
        //      过渡负责的是 HUD/大厅UI/NoS 重建, 而装职业只需要"NoS 里有本机玩家"。
        //      早先写成 `if (!Done) return;` 时, 一旦过渡在那个客户端上卡住
        //      (例如 OnGameStart 抛异常), 观战者就永远拿不到职业 —— 表现就是
        //      "观战职业分配不出来"。所以这里只尝试、不阻断。
        if (!ResumeTransition.Done)
        {
            try
            {
                ResumeTransition.Run(true);
            }
            catch { }
        }

        var lp = PlayerControl.LocalPlayer;
        if (lp == null || lp.Data == null)
        {
            Diag("本机角色还没就位");
            return;
        }

        // ⚠️ 这里【不再】要求 ShipStatus.Instance != null。
        //   中途加入的人如果是"会议进行中"进来的, 本机的船场景可能迟迟没有落地
        //   (ShipStatus 一直为空), 而全房又在等这个新玩家的资料回执 ——
        //   早先在这里直接 return, 结果是: 观战者永远装不上职业/固定不了灵魂,
        //   整房卡在"正在加载信息"的同步标签上。
        //   装职业与灵魂化只需要 玩家控制体 + NoS 玩家对象, 与船场景无关。

        if (ShipStatus.Instance == null && !_noShipLogged)
        {
            _noShipLogged = true;
            NebulaResumeEntry.Log("观战席: 本机还没有船场景(ShipStatus 为空), 仍继续收尾。");
        }

        var nosP = Virial.Game.Player.GetPlayer(lp.PlayerId);
        if (nosP == null)
        {
            Diag("本机还没被注册进 NoS");
            return;   // 等生成守卫先把它注册进本机 NoS
        }

        // ⚠️ 注意: 这里【不能】先置 _roleAssigned。
        //   置位放在 try 成功之后 —— 否则一旦本条失败(例如 NoS 换职业抛异常),
        //   就再也不会重试, 观战者会一直停在"没职业、可能还是活人"的状态。
        try
        {
            // ★ 变鬼之前先把"不要幽灵职业"写进去 —— 见 ArmNoGhostRole 的说明。
            ArmNoGhostRole(nosP);

            // ⚠️ 顺序: 先把 NoS 的死亡态标上, 再换职业。
            //   NoS 的换职业流程会按【进入那一刻】的 IsDead 决定要不要保住死亡态;
            //   先标死, 死亡态就能被换职业流程自己延续下去。
            NebulaStateCodec.MarkNoSDeadPublic(nosP);

            // 观战职业: 加载失败时回退到占位船员。
            //   绝不能留空 —— NoS 的对象没有职业时会每帧抛空引用
            //   (IsImpostor / CalcSize 等直接解引用职业)。
            object? role = null;
            try { role = SpectatorRole.MyRole; } catch { }
            var usedSpectatorRole = role != null;

            if (!usedSpectatorRole)
            {
                role = ResumeTransition.CrewRole();
                NebulaResumeEntry.Log("观战席: 观战职业不可用 → 回退占位职业(仅保证灵魂与不崩)。");
            }

            if (role != null)
            {
                try
                {
                    ResumeTransition.SetRoleLocal(nosP, role);

                    if (usedSpectatorRole)
                    {
                        NebulaResumeEntry.Log("观战席: 已分配中立观战职业(独立阵营, 不计入胜负)。");
                    }
                }
                catch (System.Exception re)
                {
                    // 反射调用会把内部异常包成 TargetInvocationException,
                    // 必须把 InnerException 打出来才能看到真正原因。
                    var inner = re is System.Reflection.TargetInvocationException tie
                        ? tie.InnerException
                        : re;

                    NebulaResumeEntry.Log("观战席: 装职业失败: "
                        + (inner != null
                            ? inner.GetType().Name + " : " + inner.Message + " | " + inner.StackTrace
                            : re.Message));
                }
            }

            // 换完职业再补一次死亡态(换职业会重建运行时对象)
            NebulaStateCodec.MarkNoSDeadPublic(nosP);

            NebulaStateCodec.MakeGhostLocal(lp, nosP, true);

            // 全部成功后才记账(失败则下轮重试)
            _roleAssigned = true;
            NebulaResumeEntry.Log("观战席: 已固定为灵魂状态。");
            ReconnectPrompt.NoteAttemptSucceeded();
        }
        catch (System.Exception e)
        {
            var inner = e is System.Reflection.TargetInvocationException tie ? tie.InnerException : e;

            NebulaResumeEntry.Log("观战席: 收尾失败(下轮重试): "
                + (inner != null
                    ? inner.GetType().Name + " : " + inner.Message + " | " + inner.StackTrace
                    : e.Message));
        }
    }
}
