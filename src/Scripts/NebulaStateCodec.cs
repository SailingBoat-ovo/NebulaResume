// ============================================================
// NebulaStateCodec.cs — 把一个玩家的 NOS 状态编码成 int[], 以及反向恢复
//
// ▸ 为什么用 int[]
//   NOS 的 RPC 参数序列化器(RemoteProcessAsset)内建支持数组, int[] 最稳。
//
// ▸ ★ 恢复走 Virial.Game.Player 的【公开接口方法】, 不用反射也不用内部类:
//       Player.SetRole(DefinedRole, int[]? arguments, RoleAssignType)
//       Player.SetGhostRole(DefinedGhostRole, int[]? arguments, RoleAssignType)
//       Player.AddModifier(DefinedModifier, int[]? arguments, RoleAssignType)
//   这三个方法在 NebulaAPI 的 Player 接口上公开, 且【没有】[NebulaRPC] 特性,
//   因此不会被 NOS 的 Harmony 劫持, 是真正的本地执行入口。
//
// ▸ ★ 职业参数(RoleArguments)必须一起恢复
//   NOS 的 RuntimeAssignable 暴露 `int[]? RoleArguments`。对参数化职业而言这不只是
//   装饰 —— 例如豺狼(Nebula.Roles.Neutral.Jackal)的参数编码了:
//        [0] 目标玩家Id  [1] 某计数  [2] 副职业计数  [3] 招募次数
//        [4] 被夺取的职业Id  [5..] 该职业的参数
//   而招募侧kick 时走的是 `SetRole(MyRole, RoleArgumentsForSidekick, ...)`
//   —— 也就是说【不恢复参数 = 恢复出一个行为错误的职业】。
//
//   ⚠️ 已知能力边界(不隐藏):
//     只恢复 assignable Id + RoleArguments 能表达的东西。
//     技能的运行期冷却 / 剩余次数 / 内部计时器不在其中 —— 它们不是通过这两个
//     字段表达的, 需要在实机确认 NOS 是否暴露了读取入口。
// ============================================================

using System.Reflection;

namespace NebulaResume;

internal static class NebulaStateCodec
{
    /// <summary>协议版本, 与服务端约定保持一致。</summary>
    private const int FormatVersion = 3;

    /// <summary>单个职业参数数组的长度上限, 防止畸形数据撑爆包。</summary>
    private const int MaxArgs = 32;

    /// <summary>附加职业数量上限。</summary>
    private const int MaxModifiers = 16;

    /// <summary>一个可分配项(职业 / 幽灵职业 / 附加职业)的 Id + 参数。</summary>
    internal struct AssignableState
    {
        public int Id;          // -1 = 无
        public int[] Args;      // 可为空数组
    }

    /// <summary>一个玩家的 NOS 状态快照(全部是整数, 便于可靠序列化)。</summary>
    /// <summary>一个任务(id + 类型 + 是否完成)。</summary>
    internal struct TaskState
    {
        public uint Id;
        public byte TypeId;
        public bool Complete;
    }

    internal struct Snapshot
    {
        public AssignableState Role;
        public AssignableState GhostRole;
        public AssignableState[] Modifiers;

        /// <summary>掉线那一刻是死是活。恢复时按它决定"灵魂"还是"活人"。</summary>
        public bool IsDead;

        // ── v3 新增 ──
        /// <summary>位置(定点化: 乘 1000 存整数)。NaN 标记 = 无位置数据。</summary>
        public int PosX;
        public int PosY;
        public bool HasPosition;

        /// <summary>任务列表(类型 + id + 是否完成)。</summary>
        public TaskState[] Tasks;
    }

    // ── 采集 ────────────────────────────────────────────────

    internal static Snapshot Capture(Player? player)
    {
        var snap = new Snapshot
        {
            Role = new AssignableState { Id = -1, Args = System.Array.Empty<int>() },
            GhostRole = new AssignableState { Id = -1, Args = System.Array.Empty<int>() },
            Modifiers = System.Array.Empty<AssignableState>(),
        };

        if (player == null)
        {
            return snap;
        }

        try
        {
            var role = player.Role;
            if (role != null)
            {
                snap.Role = CaptureAssignable(role);
            }
        }
        catch { }

        try
        {
            var ghost = player.GhostRole;
            if (ghost != null)
            {
                snap.GhostRole = CaptureAssignable(ghost);
            }
        }
        catch { }

        try
        {
            var mods = player.Modifiers;
            if (mods != null)
            {
                var list = new System.Collections.Generic.List<AssignableState>();
                foreach (var m in mods)
                {
                    if (m == null)
                    {
                        continue;
                    }

                    if (list.Count >= MaxModifiers)
                    {
                        break;
                    }

                    try
                    {
                        // RuntimeModifier.Modifier 才是 DefinedModifier
                        var st = CaptureAssignable(m.Modifier);
                        if (st.Id >= 0)
                        {
                            list.Add(st);
                        }
                    }
                    catch { }
                }

                snap.Modifiers = list.ToArray();
            }
        }
        catch { }

        try { snap.IsDead = player.IsDead; } catch { }

        // 位置与任务都要取【这名玩家自己】的角色, 不能取本机玩家:
        //   每个客户端都会替全场采集, 取本机玩家的话, 替别人上报的就全是自己的位置/任务。
        PlayerControl? target = null;
        try { target = FindControl(((IArchivedPlayer)player).PlayerId); } catch { }

        // ── v3: 位置 ──
        //   定点化(×1000)后按整数存, 避免浮点在 int[] 协议里丢精度。
        //   会议期间上报的是【会议前】的位置(见 FieldPosition)。
        try
        {
            if (target != null)
            {
                var pos = FieldPosition(target);
                snap.PosX = UnityEngine.Mathf.RoundToInt(pos.x * 1000f);
                snap.PosY = UnityEngine.Mathf.RoundToInt(pos.y * 1000f);
                snap.HasPosition = true;
            }
        }
        catch { }

        // ── v3: 任务列表 ──
        //   从香草的 PlayerInfo.Tasks 采集(那里是任务的真身)。
        try
        {
            var ctrl2 = target;
            var dataTasks = ctrl2 != null && ctrl2.Data != null ? ctrl2.Data.Tasks : null;

            if (dataTasks != null && dataTasks.Count > 0)
            {
                var list = new System.Collections.Generic.List<TaskState>(dataTasks.Count);

                for (var i = 0; i < dataTasks.Count && i < 64; i++)
                {
                    try
                    {
                        var t = dataTasks[i];
                        list.Add(new TaskState
                        {
                            Id = t.Id,
                            TypeId = t.TypeId,
                            Complete = t.Complete,
                        });
                    }
                    catch { }
                }

                snap.Tasks = list.ToArray();
            }
        }
        catch { }

        return snap;
    }

    private static PlayerControl? FindControl(byte playerId)
    {
        var all = PlayerControl.AllPlayerControls;
        if (all == null)
        {
            return null;
        }

        for (var i = 0; i < all.Count; i++)
        {
            var pc = all[i];
            if (pc != null && pc.PlayerId == playerId)
            {
                return pc;
            }
        }

        return null;
    }

    // ── 会议期间冻结位置 ──
    //
    //   会议一开始, 所有人都会被移到会议桌; 散会后还有放逐动画和选出生点。
    //   这期间如果有人掉线, 他最后一次上报的位置就是"会议桌", 散会后重连就会被放回会议桌,
    //   而不是会议前所在的地方。所以在会议 / 放逐 / 选出生点期间, 一律上报会议前的位置。
    //
    //   采集是 1 秒一次, 而"被移到会议桌"可能比本机察觉会议早一点点到达,
    //   所以冻结时用的是【上一次】采样(至少早 1 秒), 而不是最新一次。
    private static readonly System.Collections.Generic.Dictionary<byte, UnityEngine.Vector2> _fieldPos =
        new System.Collections.Generic.Dictionary<byte, UnityEngine.Vector2>();

    private static readonly System.Collections.Generic.Dictionary<byte, UnityEngine.Vector2> _fieldPosPrev =
        new System.Collections.Generic.Dictionary<byte, UnityEngine.Vector2>();

    private static readonly System.Collections.Generic.Dictionary<byte, UnityEngine.Vector2> _frozenPos =
        new System.Collections.Generic.Dictionary<byte, UnityEngine.Vector2>();

    private static int _fieldShipId;

    private static UnityEngine.Vector2 FieldPosition(PlayerControl pc)
    {
        var pos = (UnityEngine.Vector2)pc.transform.position;
        var pid = pc.PlayerId;

        // 换了一局(新的船) → 清空
        var ship = ShipStatus.Instance;
        var shipId = ship != null ? ship.GetInstanceID() : 0;
        if (shipId != _fieldShipId)
        {
            _fieldShipId = shipId;
            _fieldPos.Clear();
            _fieldPosPrev.Clear();
            _frozenPos.Clear();
        }

        var inMeeting = MeetingHud.Instance != null || ExileController.Instance != null || MeetingFlag.Active;

        if (!inMeeting)
        {
            if (_fieldPos.TryGetValue(pid, out var cur))
            {
                _fieldPosPrev[pid] = cur;
            }

            _fieldPos[pid] = pos;
            _frozenPos.Remove(pid);
            return pos;
        }

        if (!_frozenPos.TryGetValue(pid, out var frozen))
        {
            if (_fieldPosPrev.TryGetValue(pid, out var prev))
            {
                frozen = prev;
            }
            else if (_fieldPos.TryGetValue(pid, out var cur2))
            {
                frozen = cur2;
            }
            else
            {
                frozen = pos;   // 本局还没有会议外的采样(刚进来就开会): 只能用当前值
            }

            _frozenPos[pid] = frozen;
        }

        return frozen;
    }

    /// <summary>
    ///     采集一个可分配项的 Id 与它的 RoleArguments。
    ///     RuntimeRole / RuntimeModifier / RuntimeGhostRole 都继承自 RuntimeAssignable,
    ///     后者公开了 RoleArguments, 所以这里对所有种类都用同一套逻辑。
    /// </summary>
    private static AssignableState CaptureAssignable(object? assignable)
    {
        var st = new AssignableState { Id = -1, Args = System.Array.Empty<int>() };

        if (assignable == null)
        {
            return st;
        }

        // ⚠️ Id 的取法有讲究:
        //   IRoleID 只实现在【定义】类上(例如 Jackal : DefinedRoleTemplate, ..., IRoleID),
        //   而【运行时实例】不实现它(例如 Jackal+Instance : RuntimeAssignableTemplate,
        //   RuntimeRole, RuntimeAssignable, ... —— 没有 IRoleID)。
        //   所以直接 `assignable is IRoleID` 对运行时对象会静默取不到 Id(全变 -1,
        //   不报错、不崩溃, 最难查)。必须两级取值:
        //     ① 本身是定义类 → 直接取
        //     ② 本身是运行时实例 → 经 RuntimeAssignable.Assignable 拿回定义再取
        try
        {
            if (assignable is IRoleID direct)
            {
                st.Id = direct.Id;
            }
            else if (assignable is RuntimeAssignable runtimeAssignable &&
                     runtimeAssignable.Assignable is IRoleID viaDefinition)
            {
                st.Id = viaDefinition.Id;
            }
        }
        catch { }

        // RoleArguments: 参数化职业的关键数据。
        try
        {
            if (assignable is RuntimeAssignable runtime && runtime.RoleArguments is { } args)
            {
                st.Args = Clamp(args);
            }
        }
        catch { }

        return st;
    }

    private static int[] Clamp(int[] args)
    {
        if (args.Length == 0)
        {
            return System.Array.Empty<int>();
        }

        if (args.Length <= MaxArgs)
        {
            return (int[])args.Clone();
        }

        var trimmed = new int[MaxArgs];
        System.Array.Copy(args, trimmed, MaxArgs);
        return trimmed;
    }

    // ── 序列化 ──────────────────────────────────────────────
    // 布局(v2):
    //   [0] FormatVersion
    //   [1] 主职业 Id      [2] 主职业参数个数   [3..] 参数
    //   然后     幽灵职业 Id / 参数个数 / 参数
    //   然后     附加职业个数, 逐项 (Id / 参数个数 / 参数)
    //   最后     是否死亡

    internal static int[] Encode(Snapshot snap)
    {
        var outList = new System.Collections.Generic.List<int> { FormatVersion };

        AppendAssignable(outList, snap.Role);
        AppendAssignable(outList, snap.GhostRole);

        var mods = snap.Modifiers ?? System.Array.Empty<AssignableState>();
        outList.Add(mods.Length);
        for (var i = 0; i < mods.Length; i++)
        {
            AppendAssignable(outList, mods[i]);
        }

        outList.Add(snap.IsDead ? 1 : 0);

        // ── v3: 位置 ──
        outList.Add(snap.HasPosition ? 1 : 0);
        if (snap.HasPosition)
        {
            outList.Add(snap.PosX);
            outList.Add(snap.PosY);
        }

        // ── v3: 任务 ──
        var tasks = snap.Tasks ?? System.Array.Empty<TaskState>();
        outList.Add(tasks.Length);
        for (var i = 0; i < tasks.Length; i++)
        {
            outList.Add((int)tasks[i].Id);
            outList.Add(tasks[i].TypeId);
            outList.Add(tasks[i].Complete ? 1 : 0);
        }

        return outList.ToArray();
    }

    private static void AppendAssignable(System.Collections.Generic.List<int> target, AssignableState st)
    {
        var args = st.Args ?? System.Array.Empty<int>();
        target.Add(st.Id);
        target.Add(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            target.Add(args[i]);
        }
    }

    internal static Snapshot Decode(int[]? chunks)
    {
        var snap = new Snapshot
        {
            Role = new AssignableState { Id = -1, Args = System.Array.Empty<int>() },
            GhostRole = new AssignableState { Id = -1, Args = System.Array.Empty<int>() },
            Modifiers = System.Array.Empty<AssignableState>(),
        };

        if (chunks == null || chunks.Length < 2 || chunks[0] != FormatVersion)
        {
            return snap;
        }

        var i = 1;
        snap.Role = ReadAssignable(chunks, ref i);
        snap.GhostRole = ReadAssignable(chunks, ref i);

        var modCount = ReadInt(chunks, ref i);
        if (modCount < 0 || modCount > MaxModifiers)
        {
            return snap;
        }

        var mods = new System.Collections.Generic.List<AssignableState>();
        for (var m = 0; m < modCount; m++)
        {
            mods.Add(ReadAssignable(chunks, ref i));
        }

        snap.Modifiers = mods.ToArray();
        snap.IsDead = ReadInt(chunks, ref i) != 0;

        // ── v3: 位置 ──
        var hasPos = ReadInt(chunks, ref i) != 0;
        if (hasPos)
        {
            snap.PosX = ReadInt(chunks, ref i);
            snap.PosY = ReadInt(chunks, ref i);
            snap.HasPosition = true;
        }

        // ── v3: 任务 ──
        var taskCount = ReadInt(chunks, ref i);
        if (taskCount > 0 && taskCount <= 64)
        {
            var list = new System.Collections.Generic.List<TaskState>(taskCount);
            for (var t = 0; t < taskCount; t++)
            {
                var id = ReadInt(chunks, ref i);
                var type = ReadInt(chunks, ref i);
                var done = ReadInt(chunks, ref i) != 0;

                list.Add(new TaskState
                {
                    Id = (uint)id,
                    TypeId = (byte)type,
                    Complete = done,
                });
            }

            snap.Tasks = list.ToArray();
        }

        return snap;
    }

    private static AssignableState ReadAssignable(int[] chunks, ref int i)
    {
        var st = new AssignableState { Id = -1, Args = System.Array.Empty<int>() };

        st.Id = ReadInt(chunks, ref i);

        var n = ReadInt(chunks, ref i);
        if (n < 0 || n > MaxArgs)
        {
            return st;
        }

        var args = new int[n];
        for (var k = 0; k < n; k++)
        {
            args[k] = ReadInt(chunks, ref i);
        }

        st.Args = args;
        return st;
    }

    private static int ReadInt(int[] chunks, ref int i)
    {
        if (i < 0 || i >= chunks.Length)
        {
            return 0;
        }

        return chunks[i++];
    }

    // ── 恢复 ────────────────────────────────────────────────

    internal static bool Apply(Player? player, Snapshot snap, out string detail)
    {
        return Apply(player, PlayerControl.LocalPlayer, snap, out detail);
    }

    /// <summary>
    ///     把快照应用到本机。
    ///
    ///   ⚠️ 顺序(不要随意调整):
    ///        职业 → 附加职业 → 幽灵职业 → 【生死态放最后】
    ///
    ///     生死态必须在最后: 角色都建好之后才标记死亡。
    ///     否则 NoS 的死亡钩子会在【半初始化】的角色上运行 ——
    ///     职业还没设、附加职业还没挂, 钩子读到的就是错的。
    ///
    ///     反过来担心"SetRole 会把死亡态冲掉"是不必要的:
    ///     NoS 的 SetRole 判断的是【进入那一刻】的 IsDead, 而我们在它之后才设死亡,
    ///     所以不存在被冲掉的问题。
    /// </summary>
    internal static bool Apply(Player? player, PlayerControl? control, Snapshot snap, out string detail)
    {
        detail = string.Empty;

        if (player == null)
        {
            detail = "本地玩家不存在";
            return false;
        }

        var applied = 0;

        if (snap.Role.Id >= 0)
        {
            try
            {
                var role = NebulaAPI.Assignables.GetRoleById(snap.Role.Id);
                if (role != null)
                {
                    // ⚠️ 必须【广播】, 而且要带上职业参数。
                    //   以前这里只在本机生效(InvokeLocal)、参数还传的是空数组:
                    //     · 其他客户端(包括房主)看到的仍是重连时装上的占位船员 ——
                    //       房主的胜负判定按它算, 重连回来的内鬼会被当成船员;
                    //     · 职业参数(例如招募过几次)丢失。
                    //   这里直接走 NOS 的职业下发(RpcInvokerSetRole → InvokeSingle),
                    //   与开局时房主给每个人分配职业是同一条路; 跳过 SetRole 里的
                    //   "替代职业"换算, 保证恢复的就是掉线前那一个。
                    if (!InvokeAssignable(player, "RpcInvokerSetRole", typeof(DefinedRole), role, snap.Role.Args, broadcast: true))
                    {
                        player.SetRole(role, snap.Role.Args, RoleAssignType.Standard);
                    }

                    applied++;
                }
                else
                {
                    detail += "职业Id " + snap.Role.Id + " 在本版本不存在; ";
                }
            }
            catch (System.Exception e)
            {
                detail += "职业恢复异常: " + e.Message + "; ";
            }
        }

        if (snap.GhostRole.Id >= 0)
        {
            try
            {
                var ghost = NebulaAPI.Assignables.GetGhostRoleById(snap.GhostRole.Id);
                if (ghost != null)
                {
                    player.SetGhostRole(ghost, snap.GhostRole.Args, RoleAssignType.Standard);
                    applied++;
                }
            }
            catch (System.Exception e)
            {
                detail += "幽灵职业恢复异常: " + e.Message + "; ";
            }
        }

        var mods = snap.Modifiers ?? System.Array.Empty<AssignableState>();
        for (var i = 0; i < mods.Length; i++)
        {
            try
            {
                var mod = NebulaAPI.Assignables.GetModifierById(mods[i].Id);
                // 已经有的附加职业不再加: 会议中回来的人, 会议里已按旧身份装过一次,
                //   散会后正式续局再加会变成两份(例如多出一个"紧急会议"按钮)。
                if (mod != null && !HasModifier(player, mod))
                {
                    player.AddModifier(mod, mods[i].Args, RoleAssignType.Standard);
                    applied++;
                }
            }
            catch (System.Exception e)
            {
                detail += "附加职业恢复异常: " + e.Message + "; ";
            }
        }

        // ★ 生死态【放最后】: 角色全部建好之后再标记死亡。
        //   放前面的话, NoS 的死亡钩子会在半初始化的角色上运行。
        if (snap.IsDead)
        {
            if (MakeGhostLocal(control, player))
            {
                applied++;
            }
        }

        detail = "已应用 " + applied + " 项" + (detail.Length > 0 ? ("; " + detail) : string.Empty);
        return applied > 0;
    }

    // ── 灵魂化 ──────────────────────────────────────────────

    /// <summary>
    ///     在本机把玩家变成灵魂(幽灵)。顺序与 NoS 自己在 ExileExtraVictims 里的处理一致。
    ///
    ///   ⚠️ 本方法【不广播】。跨客户端的权威生死态由服务端/房主的 PlayerInfo 序列化负责,
    ///     这里做的是"让本机把他画成灵魂"。两件事都要做, 缺一不可 ——
    ///     只做本机的话, 房主下一次序列化会把本机重新拉回活人态,
    ///     表现就是玩家看到的「我这边是灵魂, 别人那边我是活人」。
    /// </summary>
    /// <summary>
    ///     把本机玩家恢复为【活人】状态(掉线前是活的)。
    ///
    ///   为什么需要主动写: 中途生成的身体在入场过程中可能被置过死亡
    ///   (例如服务端/房主的保护逻辑、或上一次状态残留), 不纠正的话
    ///   会出现"活着回来却动不了/是鬼"的现象。
    ///
    ///   这里只清两层标记, 不复活别人、不改胜负数据。
    /// </summary>
    internal static bool MakeAliveLocal(PlayerControl? control, Player? nosPlayer = null)
    {
        if (control == null)
        {
            return false;
        }

        var ok = false;

        try
        {
            // ① 旧版数据层: playersArray[id].Revive() + 清死亡名单
            //    NOS 的胜负判定/统计读的是这一层, 不写它就会出现
            //    "香草层是活人、NOS 仍认为他死了"。
            try
            {
                LegacyPlayerData.Revive(control.PlayerId);
            }
            catch (System.Exception ge)
            {
                NebulaResumeEntry.Log("复活(旧版数据层)异常: " + ge.Message);
            }

            // ② 香草层: 视觉 + 数据标记
            if (control.Data != null && control.Data.IsDead)
            {
                try { control.Revive(); } catch { }
                try
                {
                    control.Data.IsDead = false;
                    control.Data.MarkDirty();
                }
                catch { }
            }

            try { control.moveable = true; } catch { }
            ok = true;
        }
        catch { }

        // 模组层
        try
        {
            nosPlayer ??= Player.GetPlayer(control.PlayerId);
            MarkNoSAlive(nosPlayer);
            ok = true;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("复活(模组层)异常: " + e.Message);
        }

        return ok;
    }

    /// <summary>
    ///     把位置恢复到掉线时的坐标。
    ///
    ///   用 RpcSnapTo 而不是直接改 transform: 后者会在下一帧被网络同步覆盖回去,
    ///   玩家会看到自己"闪回"到出生点。RpcSnapTo 是香草自带的权威瞬移。
    /// </summary>
    internal static bool ApplyPosition(PlayerControl? control, Snapshot snap)
    {
        if (control == null || !snap.HasPosition)
        {
            return false;
        }

        try
        {
            var pos = new UnityEngine.Vector2(snap.PosX / 1000f, snap.PosY / 1000f);

            var net = control.NetTransform;
            if (net != null)
            {
                net.RpcSnapTo(pos);
                NebulaResumeEntry.Log("位置恢复: 已瞬移到 (" + pos.x.ToString("F2")
                    + ", " + pos.y.ToString("F2") + ")。");
                return true;
            }

            control.transform.position = new UnityEngine.Vector3(pos.x, pos.y, control.transform.position.z);
            return true;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("位置恢复失败: " + e.Message);
            return false;
        }
    }

    /// <summary>
    ///     把任务列表恢复到掉线前的状态。
    ///
    ///   两个来源, 依次尝试:
    ///     ① 状态包里的任务(我们自己采集的, 不依赖服务端补发资料)
    ///     ② 香草 PlayerInfo.Tasks(服务端补发玩家资料后才有)
    ///
    ///   香草对中途加入者只填了数据层, 不会建 myTasks(HUD 显示 + 真正可做那份),
    ///   所以必须显式 SetTasks 一次, 再按完成状态补标。
    /// </summary>
    internal static bool ApplyTasks(PlayerControl? control, Snapshot snap)
    {
        if (control == null)
        {
            return false;
        }

        try
        {
            var tasks = snap.Tasks ?? System.Array.Empty<TaskState>();

            // 回退: 状态包没有任务时, 用香草资料层
            if (tasks.Length == 0)
            {
                var dt = control.Data != null ? control.Data.Tasks : null;
                if (dt == null || dt.Count == 0)
                {
                    NebulaResumeEntry.Log("任务恢复: 无可用任务数据。");
                    return false;
                }

                control.SetTasks(dt);
                NebulaResumeEntry.Log("任务恢复: 用资料层任务重建 " + dt.Count + " 个。");
                return true;
            }

            // ══════════════════════════════════════════════════════
            //   按 NOS 的设计, 任务是两步:
            //
            //   ① NetworkedPlayerInfo.SetTasks(Il2CppStructArray<byte>)
            //      → 填【数据层】Data.Tasks。这一步 NOS 有 Prefix 拦截,
            //        会在里面做 InitialTasks 记录 + RoleAction(OnSetTasks),
            //        所以【必须走这个方法】, 不能直接给 Data.Tasks 赋字段。
            //
            //   ② PlayerControl.SetTasks(List<TaskInfo>)
            //      → 填【可玩层】myTasks。HUD 显示、玩家实际做任务用的是这个。
            //        它内部是协程(CoSetTasks), 下一帧之后才建好。
            // ══════════════════════════════════════════════════════

            // ① 数据层: 用字节数组走香草入口
            try
            {
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(tasks.Length);

                for (var i2 = 0; i2 < tasks.Length; i2++)
                {
                    arr[i2] = tasks[i2].TypeId;
                }

                control.Data.SetTasks(arr);
                NebulaResumeEntry.Log("任务恢复: 数据层已写入 " + tasks.Length + " 个(typeId 数组)。");
            }
            catch (System.Exception de)
            {
                NebulaResumeEntry.Log("任务恢复: 数据层写入失败: " + de.Message);
            }

            // ② 在 Data.Tasks 上标完成
            var marked = 0;
            try
            {
                var dataTasks = control.Data != null ? control.Data.Tasks : null;

                if (dataTasks != null)
                {
                    var n = System.Math.Min(tasks.Length, dataTasks.Count);

                    for (var i3 = 0; i3 < n; i3++)
                    {
                        if (!tasks[i3].Complete)
                        {
                            continue;
                        }

                        try
                        {
                            var ti = dataTasks[i3];
                            var t = ti.GetType();
                            var prop = t.GetProperty("Complete",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                            if (prop != null && prop.CanWrite)
                            {
                                prop.SetValue(ti, true);
                            }
                            else
                            {
                                t.GetField("Complete",
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                    ?.SetValue(ti, true);
                            }

                            marked++;
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // ③ 可玩层: 用 Data.Tasks 建 myTasks(这一步必须是 List<TaskInfo>)
            var myCount = -1;
            try
            {
                var dataTasks = control.Data != null ? control.Data.Tasks : null;

                if (dataTasks != null && dataTasks.Count > 0)
                {
                    control.SetTasks(dataTasks);
                }

                if (control.myTasks != null)
                {
                    myCount = control.myTasks.Count;
                }
            }
            catch (System.Exception se)
            {
                NebulaResumeEntry.Log("任务恢复: PlayerControl.SetTasks 异常: " + se.Message);
            }

            // ④ 通知 NOS 任务计数
            try
            {
                LegacyTaskCount.Notify(control.PlayerId, tasks.Length);
            }
            catch { }

            NebulaResumeEntry.Log("任务恢复: 数据层=" + tasks.Length
                + " 标完成=" + marked
                + " myTasks(当帧)=" + myCount
                + " | CoSetTasks 是协程, 稍后复查");

            // ⑤ 排程复查 + 补标完成
            _pendingTaskExpect = tasks.Length;

            var completes = new bool[tasks.Length];
            for (var ic = 0; ic < tasks.Length; ic++)
            {
                completes[ic] = tasks[ic].Complete;
            }

            _pendingTaskSnapshotCompletes = completes;
            _pendingTaskCheckAt = UnityEngine.Time.unscaledTime + 1.5f;

            return true;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("任务恢复失败: " + e.Message);
            return false;
        }
    }

    // ── 任务协程的延迟复查 ──
    private static int _pendingTaskExpect;
    private static bool[]? _pendingTaskSnapshotCompletes;
    private static float _pendingTaskCheckAt;

    /// <summary>由 Entry.Tick 每帧调用: 约 1 秒后复查 myTasks 是否真的建出来了。</summary>
    internal static void TickTaskVerify()
    {
        if (_pendingTaskCheckAt <= 0f)
        {
            return;
        }

        if (UnityEngine.Time.unscaledTime < _pendingTaskCheckAt)
        {
            return;
        }

        _pendingTaskCheckAt = 0f;

        try
        {
            var control = PlayerControl.LocalPlayer;
            if (control == null)
            {
                return;
            }

            var myTasks = control.myTasks != null ? control.myTasks.Count : 0;
            var dataTasks = control.Data != null && control.Data.Tasks != null ? control.Data.Tasks.Count : 0;

            if (myTasks > 0)
            {
                NebulaResumeEntry.Log("任务复查: OK | 期望=" + _pendingTaskExpect
                    + " myTasks=" + myTasks + " Data.Tasks=" + dataTasks);
            }
            else
            {
                NebulaResumeEntry.Log("任务复查: 失败 | 期望=" + _pendingTaskExpect
                    + " myTasks=0 Data.Tasks=" + dataTasks
                    + " | CoSetTasks 未完成 → 改为直接建任务对象");

                // 协程不可靠(中途进来的人常常在里面就中断了), 直接按 Data.Tasks 建。
                myTasks = BuildMyTasksDirect(control);
                NebulaResumeEntry.Log("任务复查: 直接建出 myTasks=" + myTasks + "。");
            }

            // 补标完成: CoSetTasks 重建出来的任务一律是"未完成",
            // 所以要在它建好之后, 按状态包里的完成标记再补一次。
            if (myTasks > 0 && _pendingTaskSnapshotCompletes != null)
            {
                var marked = 0;
                var data = control.Data;
                var n = data != null && data.Tasks != null
                    ? System.Math.Min(data.Tasks.Count, _pendingTaskSnapshotCompletes.Length)
                    : 0;

                // 按任务 Id 对应, 不按下标: 内鬼的任务头会插在 myTasks 最前面, 下标会错位。
                for (var i = 0; i < n; i++)
                {
                    if (!_pendingTaskSnapshotCompletes[i])
                    {
                        continue;
                    }

                    try
                    {
                        var id = data!.Tasks[i].Id;

                        for (var j = 0; j < control.myTasks.Count; j++)
                        {
                            var t = control.myTasks[j];
                            if (t == null || t.Id != id || t.TryCast<NormalPlayerTask>() == null)
                            {
                                continue;
                            }

                            if (!t.IsComplete)
                            {
                                t.Complete();
                            }

                            marked++;
                            break;
                        }
                    }
                    catch { }
                }

                if (marked > 0)
                {
                    NebulaResumeEntry.Log("任务复查: 已补标完成 " + marked + " 个。");
                }
            }

            _pendingTaskSnapshotCompletes = null;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("任务复查异常: " + e.Message);
        }
    }

    /// <summary>
    ///     不经过 CoSetTasks 协程, 当帧把 Data.Tasks 建成 myTasks。
    ///
    ///   ▸ 为什么
    ///     PlayerControl.SetTasks 只是启动 CoSetTasks 协程, 协程里要先等船、
    ///     再生成任务头(依赖香草 Data.Role), 然后逐个实例化任务。
    ///     中途进来的人任意一步出异常, 协程就静默结束 —— 日志表现为
    ///     "1.5 秒后 myTasks=0, Data.Tasks=6"。
    ///     这里按协程同样的步骤做一遍, 每一步单独兜住, 一个失败不影响其余。
    /// </summary>
    /// <returns>建好后 myTasks 的数量。</returns>
    private static int BuildMyTasksDirect(PlayerControl control)
    {
        try
        {
            var ship = ShipStatus.Instance;
            var data = control.Data;
            if (ship == null || data == null || data.Tasks == null || control.myTasks == null)
            {
                NebulaResumeEntry.Log("任务直建: 条件不足(船/资料/列表为空), 跳过。");
                return control.myTasks != null ? control.myTasks.Count : 0;
            }

            // 清掉可能残留的半成品(协程中断时可能已经建了一部分)。
            try
            {
                for (var i = 0; i < control.myTasks.Count; i++)
                {
                    var old = control.myTasks[i];
                    if (old != null)
                    {
                        UnityEngine.Object.Destroy(old.gameObject);
                    }
                }

                control.myTasks.Clear();
            }
            catch { }

            // 任务头(职业说明那一行): 依赖香草 Data.Role, 失败不影响任务本身。
            try
            {
                if (data.Role != null)
                {
                    data.Role.SpawnTaskHeader(control);
                }
            }
            catch (System.Exception he)
            {
                NebulaResumeEntry.Log("任务直建: 任务头生成失败(忽略): " + he.Message);
            }

            var built = 0;
            for (var i = 0; i < data.Tasks.Count; i++)
            {
                try
                {
                    var info = data.Tasks[i];
                    if (info == null)
                    {
                        continue;
                    }

                    var prefab = ship.GetTaskById(info.TypeId);
                    if (prefab == null)
                    {
                        NebulaResumeEntry.Log("任务直建: 找不到任务模板 typeId=" + info.TypeId + "。");
                        continue;
                    }

                    var task = UnityEngine.Object.Instantiate(prefab, control.transform);
                    task.Id = info.Id;
                    task.Owner = control;
                    task.Initialize();
                    control.myTasks.Add(task);
                    built++;
                }
                catch (System.Exception te)
                {
                    NebulaResumeEntry.Log("任务直建: 第 " + i + " 个失败: " + te.Message);
                }
            }

            try
            {
                var hud = HudManager.Instance;
                if (hud != null && hud.TaskStuff != null)
                {
                    hud.TaskStuff.SetActive(true);
                }
            }
            catch { }

            return built;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("任务直建异常: " + e.GetType().Name + " : " + e.Message);
            return 0;
        }
    }

    /// <summary>供其它模块写 NoS 死亡态(换职业前后都要写, 见 SpectatorSeat)。</summary>
    internal static void MarkNoSDeadPublic(Player? nosPlayer) => MarkNoSDead(nosPlayer);

    internal static bool MakeGhostLocal(PlayerControl? control, Player? nosPlayer = null,
        bool forceDie = false)
    {
        if (control == null)
        {
            return false;
        }

        var ok = false;

        // ── 香草层(本机表现) ──
        try
        {
            // ── 关键: Die() 才是"把实体切成幽灵外观"的那一步 ──
            //
            //   Data.IsDead = true 只改【数据】, 不会让渲染层变成幽灵;
            //   真正把角色模型切成幽灵(半透明、穿墙)的是 Die() 走的死亡流程。
            //
            //   ⚠️ 注意: 不能写成「只在 !Data.IsDead 时才 Die」。
            //      而房主会先把 Data.IsDead 广播成 true, 于是重连者客户端跳过 Die()
            //      → 角色的数据是死的、外观却还是活人实体 → "身体错误显示出来"。
            //      对中途生成的重连者必须强制走一次, 不能因为数据已是 death 就跳过。
            //
            //   Die(DeathReason.Kill, false) 的第二个参数 = 不指定幽灵职业,
            //   它【不会】生成尸体(尸体只由 MurderPlayer 生成)。
            if (control.Data != null && !control.Data.IsDead)
            {
                try { control.Die(DeathReason.Kill, false); } catch { }
            }
            else if (forceDie)
            {
                try { control.Die(DeathReason.Kill, false); } catch { }
            }

            // ★ IsDead 改动后必须 MarkDirty()。
            //   PlayerInfo 的归属是【玩家自己的客户端】—— 只有归属方标脏并序列化,
            //   服务端才会把它转发给其他客户端。只改本机不标脏 = 别人永远看到活人
            //   (症状就是自己看是鬼、别人看是活人、还能被击杀)。
            try
            {
                if (control.Data != null)
                {
                    control.Data.IsDead = true;
                    control.Data.MarkDirty();
                }
            }
            catch { }

            // ③ 旧版数据层: Die + 死亡名单(NOS 胜负判定读这一层)
            try
            {
                LegacyPlayerData.Die(control.PlayerId);
            }
            catch (System.Exception ge)
            {
                NebulaResumeEntry.Log("灵魂化(旧版数据层)异常: " + ge.Message);
            }

            // ④ 死人不该卡在管道里、不该失去移动能力(与 NOS 的 ResetOnDying 等价)
            try { control.inVent = false; } catch { }
            try { control.moveable = true; } catch { }

            ok = true;
        }
        catch
        {
        }

        // ── NoS 层(真正决定 HUD/显示认为你是不是鬼) ──
        //
        //   ⚠️ 只做香草层是不够的: NoS 的 Player.IsDead 是带后备字段的自动属性,
        //      与香草的 Data.IsDead 无关。不写它的话, 本机 NoS 层仍以为自己是活人,
        //      表现就是「重连后不是鬼魂」。
        try
        {
            nosPlayer ??= Player.GetPlayer(control.PlayerId);
            MarkNoSDead(nosPlayer);
            ok = true;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("灵魂化(NoS层)异常: " + e.Message);
        }

        return ok;
    }

    // ── NoS 层死亡写入 ──────────────────────────────────────

    private static bool _deadRouteLogged;

    private static void LogRouteOnce(string route)
    {
        if (_deadRouteLogged)
        {
            return;
        }

        _deadRouteLogged = true;
        NebulaResumeEntry.Log("生死同步: NoS 死亡标记走的是「" + route + "」。");
    }

    internal static void MarkNoSDead(object? nosPlayer)
    {
        if (nosPlayer == null)
        {
            return;
        }

        try
        {
            var t = nosPlayer.GetType();

            // 显示用字段(会议列表 / 终局统计会读)
            try
            {
                var dts = t.GetField("DeathTimeStamp", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (dts != null && dts.GetValue(nosPlayer) == null)
                {
                    dts.SetValue(nosPlayer, 0f);
                }
            }
            catch { }

            try
            {
                var psType = System.Type.GetType("Nebula.Player.PlayerState");
                if (psType == null)
                {
                    var asms = System.AppDomain.CurrentDomain.GetAssemblies();
                    for (var i = 0; i < asms.Length && psType == null; i++)
                    {
                        try { psType = asms[i].GetType("Nebula.Player.PlayerState", false); } catch { }
                    }
                }

                var deadField = psType?.GetField("Dead", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var deadValue = deadField?.GetValue(null);
                if (deadValue != null)
                {
                    t.GetField("MyState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.SetValue(nosPlayer, deadValue);
                }
            }
            catch { }

            if (WriteNoSIsDead(nosPlayer, t, true))
            {
                return;
            }

            NebulaResumeEntry.Log("【严重】写 NoS IsDead 失败: UpdateModDead / setter / 后备字段都找不到。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("标记 NoS 死亡失败: " + e.Message);
        }
    }

    /// <summary>
    ///     把模组层的死亡态写回"存活"(与 MarkNoSDead 对称)。
    ///     PlayerModInfo.IsDead 是带后备字段的自动属性, setter 私有,
    ///     所以与 MarkNoSDead 走同一套三级降级写入。
    /// </summary>
    internal static void MarkNoSAlive(object? nosPlayer)
    {
        if (nosPlayer == null)
        {
            return;
        }

        try
        {
            var t = nosPlayer.GetType();

            // ★ 关键: IsDead 的定义是
            //     public bool IsDead { get => IsDisconnected || field; private set => field = value; }
            //   只要 IsDisconnected 还是 true, 就算 field 写成 false, IsDead 依然返回 true
            //   —— 表现就是"重连后是活人, 但杀不死"。
            //   所以复活必须同时清 IsDisconnected。
            try
            {
                var disc = t.GetProperty("IsDisconnected",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (disc != null && disc.CanWrite)
                {
                    disc.SetValue(nosPlayer, false);
                }
                else
                {
                    var df = t.GetField("<IsDisconnected>k__BackingField",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    df?.SetValue(nosPlayer, false);
                }
            }
            catch (System.Exception de)
            {
                NebulaResumeEntry.Log("清 IsDisconnected 失败: " + de.Message);
            }

            WriteNoSIsDead(nosPlayer, t, false);

            // 校验: 读完确认真的活了
            try
            {
                var isDeadProp = t.GetProperty("IsDead",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var stillDead = isDeadProp?.GetValue(nosPlayer) is bool b && b;
                NebulaResumeEntry.Log("NoS 存活写入: IsDead 现在 = " + (!stillDead));
            }
            catch { }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("标记 NoS 存活失败: " + e.Message);
        }
    }

    /// <summary>三级降级写入 NoS 的 IsDead。</summary>
    private static bool WriteNoSIsDead(object nosPlayer, System.Type t, bool value)
    {
        // ① 官方入口: internal void UpdateModDead(bool)
        try
        {
            var m = t.GetMethod("UpdateModDead",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, new[] { typeof(bool) }, null);

            if (m != null)
            {
                m.Invoke(nosPlayer, new object[] { value });
                LogRouteOnce("UpdateModDead(bool)");
                return true;
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("写 NoS IsDead: UpdateModDead 失败: " + e.Message);
        }

        // ② private setter
        try
        {
            var setter = t.GetProperty("IsDead", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(nosPlayer, new object[] { value });
                LogRouteOnce("IsDead 私有 setter");
                return true;
            }
        }
        catch { }

        // ③ 自动属性后备字段
        try
        {
            var f = t.GetField("<IsDead>k__BackingField", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null)
            {
                f.SetValue(nosPlayer, value);
                LogRouteOnce("<IsDead>k__BackingField");
                return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    ///     通过 NOS 的下发入口给 NoS 玩家装 职业 / 幽灵职业 / 附加职业。
    ///     broadcast = true 走 InvokeSingle(全场), false 走 InvokeLocal(只在本机)。成功返回 true。
    /// </summary>
    /// <param name="invokerMethod">RpcInvokerSetRole / RpcInvokerSetGhostRole / RpcInvokerSetModifier</param>
    private static bool InvokeAssignable(object nosPlayer, string invokerMethod, System.Type definedType,
        object defined, int[]? args, bool broadcast)
    {
        if (nosPlayer == null || defined == null)
        {
            return false;
        }

        try
        {
            var m = nosPlayer.GetType().GetMethod(
                invokerMethod,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { definedType, typeof(int[]), typeof(RoleAssignType) },
                null);

            if (m == null)
            {
                return false;
            }

            var invoker = m.Invoke(nosPlayer, new object[]
            {
                defined,
                args ?? System.Array.Empty<int>(),
                RoleAssignType.Standard,
            });

            if (invoker == null)
            {
                return false;
            }

            var fire = invoker.GetType().GetMethod(
                broadcast ? "InvokeSingle" : "InvokeLocal",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                System.Type.EmptyTypes,
                null);

            if (fire == null)
            {
                return false;
            }

            fire.Invoke(invoker, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    ///     只在【本机】把别的玩家的 职业 / 幽灵职业 / 附加职业 设成快照里的样子(不广播)。
    /// </summary>
    /// <remarks>
    ///     给中途回来的人补齐"别人是什么职业": NOS 只在开局时把每个人的职业下发一次,
    ///     中途进来的人错过了, 本机只能给其他人装占位船员 —— 于是在他眼里全场都是船员。
    ///     数据由房主提供(见 RoleSync)。只改本机这一份, 其他客户端上这些玩家的职业状态
    ///     (冷却、技能次数等)完全不受影响。不动生死、位置、任务。
    /// </remarks>
    internal static bool ApplyRolesLocal(Player? subject, Snapshot snap, out string detail)
    {
        detail = string.Empty;

        if (subject == null)
        {
            detail = "NoS 中没有这名玩家";
            return false;
        }

        var applied = 0;

        try
        {
            if (snap.Role.Id >= 0)
            {
                var role = NebulaAPI.Assignables.GetRoleById(snap.Role.Id);
                if (role != null && InvokeAssignable(subject, "RpcInvokerSetRole", typeof(DefinedRole), role, snap.Role.Args, broadcast: false))
                {
                    applied++;
                }
            }

            if (snap.GhostRole.Id >= 0)
            {
                var ghost = NebulaAPI.Assignables.GetGhostRoleById(snap.GhostRole.Id);
                if (ghost != null && InvokeAssignable(subject, "RpcInvokerSetGhostRole", typeof(DefinedGhostRole), ghost, snap.GhostRole.Args, broadcast: false))
                {
                    applied++;
                }
            }

            var mods = snap.Modifiers ?? System.Array.Empty<AssignableState>();
            for (var i = 0; i < mods.Length; i++)
            {
                var mod = NebulaAPI.Assignables.GetModifierById(mods[i].Id);
                if (mod == null || HasModifier(subject, mod))
                {
                    continue;   // 已经有了就不重复挂(重复收到同步时不会叠加)
                }

                if (InvokeAssignable(subject, "RpcInvokerSetModifier", typeof(DefinedModifier), mod, mods[i].Args, broadcast: false))
                {
                    applied++;
                }
            }
        }
        catch (System.Exception e)
        {
            detail = "异常: " + e.Message;
        }

        detail = "已在本机应用 " + applied + " 项" + (detail.Length > 0 ? " (" + detail + ")" : "");
        return applied > 0;
    }

    private static bool HasModifier(Player subject, DefinedModifier modifier)
    {
        try
        {
            foreach (var m in subject.Modifiers)
            {
                if (m != null && m.Modifier == modifier)
                {
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

}
