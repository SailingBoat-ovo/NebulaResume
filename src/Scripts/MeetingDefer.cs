// ============================================================
// MeetingDefer.cs — 会议中进入: 本机角色到齐之前先不搭会议界面
//
// ▸ 现象(实测)
//   会议进行中回到房间的人, 会议界面完全不出现。日志:
//       NOS 开局: AchievementManagerModule.OnGameStart → HasTag(本机玩家=null)
//       PlayerVoteArea.SetCosmetics → CosmeticsCache.GetNameplate  空引用
//       MeetingHud.Start 中断
//
// ▸ 原因
//   会议对象随房间数据【最先】到达, Unity 立刻调用它的 Start;
//   而本机的角色是房主之后才生成的。Start 那一刻:
//     · 本机角色还不存在 → NoS 的开会补丁读本机玩家的职业, 读到空直接中断;
//     · 船上的装扮缓存是空的(没经历开局介绍) → 投票格取名牌空引用。
//   Start 只会被 Unity 调用一次, 中断之后界面就永远是半成品。
//
// ▸ 做法
//   本机是"中途进来、还没续局"的机器, 且角色/装扮还没到齐时:
//     ① 拦下这一次 Start(先放一个空的投票格数组, 让每帧更新不报错);
//     ② 立刻开始载入全场装扮;
//     ③ 每帧检查: 本机角色、资料、原版职业到齐 + 全场名牌可取 →
//        先重建本机 NoS 层, 再由本模块手动调用 Start, 界面按正常流程完整搭出来;
//     ④ 开会动画是开会那一刻才放的, 中途进来的人没有 → 按房主广播的会议阶段和
//        倒计时直接对齐(讨论中 / 投票中), 投票按钮照常可用。
//   等太久(角色迟迟不来)就照常搭, 不比原来更差。
//
// ▸ 只影响中途进来的机器
//   正常开局的机器在会议开始时 NoS 早已在对局阶段, 本模块一律放行, 什么都不做。
// ============================================================

using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;

namespace NebulaResume;

internal static class MeetingDefer
{
    /// <summary>被拦下、等着补搭的会议对象。</summary>
    private static MeetingHud? _deferred;
    private static float _deferredAt;
    private static float _readyAt;
    private static float _cosmeticsKickAt;
    private static float _logAt;
    private static float _probeAt;
    private static bool _cosmeticsOk;

    /// <summary>本机是会议中进来的人, 会议期间也要藏住大厅界面。</summary>
    internal static bool Engaged => _deferred != null || _catchUp != null || _intro != null || MeetingSeat.Active;

    /// <summary>下一次 Start 由本模块发起, 前缀直接放行。</summary>
    private static bool _allowNext;

    /// <summary>补搭完成后要对齐阶段的会议对象。</summary>
    private static MeetingHud? _catchUp;
    private static float _catchUpAt;

    /// <summary>角色到齐后再等装扮的最长时间(秒)。</summary>
    private const float CosmeticsWait = 5f;

    /// <summary>角色迟迟不来时的最长等待(秒)。超过就照常搭(不比原来更差)。</summary>
    private const float GiveUpSeconds = 30f;

    internal static bool Deferring => _deferred != null;

    internal static void Reset()
    {
        _deferred = null;
        _catchUp = null;
        _intro = null;
        _diagMeeting = null;
        _savedVotes.Clear();
        _allowNext = false;
        _readyAt = 0f;
    }

    internal static void Install(Harmony harmony)
    {
        try
        {
            var m = AccessTools.Method(typeof(MeetingHud), "Start");
            if (m == null)
            {
                NebulaResumeEntry.Log("会议延迟搭建: 未找到 MeetingHud.Start, 跳过。");
                return;
            }

            var prefix = new HarmonyMethod(typeof(MeetingDefer).GetMethod(nameof(StartPrefix),
                BindingFlags.Static | BindingFlags.NonPublic))
            {
                priority = Priority.First,
            };
            harmony.Patch(m, prefix: prefix);
            NebulaResumeEntry.Log("会议延迟搭建: 已挂 MeetingHud.Start 前缀。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议延迟搭建: 挂载失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>MeetingHud.Start 前缀: 返回 false = 这一次先不搭。</summary>
    private static bool StartPrefix(MeetingHud __instance)
    {
        if (_allowNext)
        {
            _allowNext = false;
            return true;
        }

        try
        {
            if (!IsMidJoinMachine())
            {
                return true;   // 正常机器: 照常
            }

            if (LocalReady(out _) && CosmeticsReady())
            {
                // 中途进来, 但该到的都到了: 照常搭(NoS 层由外观预修前缀先补)。
                // 开会动画同样没有 → 搭完后照样摆到屏幕上并对齐进度。
                _catchUp = __instance;
                _catchUpAt = UnityEngine.Time.unscaledTime + 0.3f;
                return true;
            }

            // 拦下。投票格在会议对象同步过来那一刻就已经生成了(当时装扮缺失, 外观是坏的),
            //   这里不动它们, 补搭时整体重建; 只有还没生成时才放一个空数组, 免得每帧更新空引用。
            try
            {
                if (__instance.playerStates == null)
                {
                    __instance.playerStates = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea>(0);
                }
            }
            catch { }

            _deferred = __instance;
            _deferredAt = UnityEngine.Time.unscaledTime;
            _readyAt = 0f;
            _cosmeticsKickAt = 0f;
            _logAt = 0f;
            _probeAt = 0f;
            _cosmeticsOk = false;
            KickCosmetics();

            LocalReady(out var why);
            NebulaResumeEntry.Log("会议延迟搭建: 本机还没准备好(" + why + ") → 会议界面推迟到角色与装扮到齐后再搭。");
            return false;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议延迟搭建: 判定异常, 照常搭建: " + e.Message);
            return true;
        }
    }

    /// <summary>由 Entry.Tick 每帧调用。</summary>
    internal static void Tick()
    {
        TickCatchUp();
        TickDiag();

        var meeting = _deferred;
        if (meeting == null)
        {
            return;
        }

        // 会议已经结束(对象被销毁)。
        if (MeetingHud.Instance == null || MeetingHud.Instance.Pointer != meeting.Pointer)
        {
            NebulaResumeEntry.Log("会议延迟搭建: 会议在补搭前已结束。");
            _deferred = null;
            return;
        }

        var now = UnityEngine.Time.unscaledTime;

        // 装扮每 2 秒补载一次(本机角色到达后, 自己那一份也要载入)。
        if (now >= _cosmeticsKickAt)
        {
            KickCosmetics();
        }

        var localOk = LocalReady(out var why);
        if (localOk && _readyAt <= 0f)
        {
            _readyAt = now;
        }

        // 名牌探测每 0.5 秒一次: 缺条目时游戏会记一条错误日志, 每帧探测会刷屏。
        if (localOk && now >= _probeAt)
        {
            _probeAt = now + 0.5f;
            _cosmeticsOk = CosmeticsReady();
        }

        var cosmeticsOk = _cosmeticsOk;
        var go = localOk && (cosmeticsOk || now - _readyAt >= CosmeticsWait);
        var giveUp = now - _deferredAt >= GiveUpSeconds;

        if (!go && !giveUp)
        {
            if (now >= _logAt)
            {
                _logAt = now + 3f;
                NebulaResumeEntry.Log("会议延迟搭建: 等待中(" + (localOk ? "装扮载入中" : why) + ")。");
            }

            return;
        }

        _deferred = null;

        if (!localOk)
        {
            // 角色一直没来: NoS 开会补丁必然读不到本机玩家。照常搭, 结果不比原来更差;
            // 散会后服务端照常续局。
            NebulaResumeEntry.Log("会议延迟搭建: 等待超过 " + (int)GiveUpSeconds + " 秒仍无本机角色 → 照常搭建。");
        }
        else
        {
            NebulaResumeEntry.Log("会议延迟搭建: 已就绪(装扮" + (cosmeticsOk ? "已载入" : "未完全载入") + ") → 先重建本机 NoS, 再搭会议界面。");
        }

        try { ResumeTransition.PrepareForMeeting(); } catch { }
        try { ResumeTransition.EnsureLocalNoS(); } catch { }
        try { OutfitSync.FixAllNow(); } catch { }

        // 重建投票格: 同步那一刻生成的投票格外观是坏的(装扮当时还没载入),
        //   而且模组的排序/装饰补丁当时也因为读不到本机玩家而中断。销毁后按现在的数据重新生成。
        try
        {
            var old = meeting.playerStates;
            var destroyed = 0;
            SaveVotes(old);
            if (old != null)
            {
                for (var i = 0; i < old.Length; i++)
                {
                    var area = old[i];
                    if (area != null)
                    {
                        UnityEngine.Object.Destroy(area.gameObject);
                        destroyed++;
                    }
                }
            }

            meeting.PopulateButtons();
            NebulaResumeEntry.Log("会议延迟搭建: 投票格已重建(销毁旧的 " + destroyed + " 个, 新建 "
                + (meeting.playerStates?.Length ?? 0) + " 个)。");
            RestoreVotes(meeting, "重建后");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议延迟搭建: 重建投票格出错: " + ResumeTransition.Describe(e));
        }

        try
        {
            _allowNext = true;
            meeting.Start();
            NebulaResumeEntry.Log("会议延迟搭建: 会议界面已搭建(投票格 " + (meeting.playerStates?.Length ?? 0) + " 个)。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议延迟搭建: 搭建出错: " + ResumeTransition.Describe(e));
        }
        finally
        {
            _allowNext = false;
        }

        // 下一帧开始对齐阶段(Start 里的后续协程先跑一帧)。
        _catchUp = meeting;
        _catchUpAt = now + 0.2f;
    }

    /// <summary>
    ///     中途进来的人没有开会动画: 会议停在"动画中", 讨论和投票都不会开始。
    ///     先在本机补播一遍 NoS 自己的开会动画(与平时开会完全同一段代码: 挂到 HUD、
    ///     滑入内容、介绍层、NoS 的开会初始化), 播完再按房主广播的阶段与倒计时对齐。
    ///     动画起不来或卡住时, 退回直接摆放(Present)。
    /// </summary>
    private static void TickCatchUp()
    {
        TickIntro();

        var meeting = _catchUp;
        if (meeting == null || UnityEngine.Time.unscaledTime < _catchUpAt)
        {
            return;
        }

        _catchUp = null;

        try
        {
            if (MeetingHud.Instance == null || MeetingHud.Instance.Pointer != meeting.Pointer)
            {
                return;
            }

            if (meeting.state != MeetingHud.MeetingStates.Animating)
            {
                return;   // 已经在走了(例如动画补上了), 不干预
            }

            // 职业: 重建时全场(含自己)装的是占位船员, 正式的状态包要散会后才来。
            //   现在就向房主要全场职业; 回来的旧座位职业会同时套到本机身上(只在本机, 不广播)。
            try { RoleSync.RequestAfterTransition(); } catch { }

            if (StartNosIntro(meeting))
            {
                _intro = meeting;
                _introStartedAt = UnityEngine.Time.unscaledTime;
                return;
            }

            Present(meeting);
            AfterShown(meeting, "直接摆放");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议对齐异常: " + e.Message);
        }
    }

    /// <summary>正在补播开会动画的会议对象。</summary>
    private static MeetingHud? _intro;
    private static float _introStartedAt;

    /// <summary>开会动画最长等多久(秒)。正常 4~6 秒。</summary>
    private const float IntroTimeout = 15f;

    /// <summary>等补播的开会动画结束(状态离开"动画中"), 然后对齐进度。</summary>
    private static void TickIntro()
    {
        var meeting = _intro;
        if (meeting == null)
        {
            return;
        }

        try
        {
            if (MeetingHud.Instance == null || MeetingHud.Instance.Pointer != meeting.Pointer)
            {
                _intro = null;
                return;
            }

            if (meeting.state == MeetingHud.MeetingStates.Animating)
            {
                if (UnityEngine.Time.unscaledTime - _introStartedAt < IntroTimeout)
                {
                    return;
                }

                NebulaResumeEntry.Log("会议显示: 开会动画 " + (int)IntroTimeout + " 秒没播完 → 改为直接摆放。");
                _intro = null;
                Present(meeting);
                AfterShown(meeting, "动画超时后直接摆放");
                return;
            }

            _intro = null;
            AfterShown(meeting, "开会动画");
        }
        catch (System.Exception e)
        {
            _intro = null;
            NebulaResumeEntry.Log("会议显示: 等待开会动画异常: " + e.Message);
        }
    }

    /// <summary>在本机补播 NoS 的开会动画(紧急会议样式)。成功启动返回 true。</summary>
    private static bool StartNosIntro(MeetingHud meeting)
    {
        try
        {
            var ext = ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension");
            var m = ext?.GetMethod("ModCoMeetingHudIntro", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            var reportType = ext?.GetNestedType("ReportType", BindingFlags.NonPublic | BindingFlags.Public);
            if (m == null || reportType == null)
            {
                NebulaResumeEntry.Log("会议显示: 找不到 NoS 开会动画, 改为直接摆放。");
                return false;
            }

            // 报告人: 本机有他的身体就用他, 否则用本机自己(只影响动画里那张卡片)。
            PlayerControl? reporter = null;
            try
            {
                var info = GameData.Instance != null ? GameData.Instance.GetPlayerById(meeting.reporterId) : null;
                if (info != null && info.Object != null)
                {
                    reporter = info.Object;
                }
            }
            catch { }

            reporter ??= PlayerControl.LocalPlayer;
            if (reporter == null || reporter.Data == null)
            {
                return false;
            }

            var routine = m.Invoke(null, new object?[]
            {
                meeting,
                reporter,
                null,
                System.Enum.ToObject(reportType, 0),   // EmergencyMeeting: 不需要尸体资料
            }) as System.Collections.IEnumerator;

            if (routine == null)
            {
                return false;
            }

            meeting.StartCoroutine(BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions.WrapToIl2Cpp(routine));
            NebulaResumeEntry.Log("会议显示: 已在本机补播开会动画(与正常开会同一流程)。");
            return true;
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议显示: 补播开会动画失败: " + ResumeTransition.Describe(e) + " → 改为直接摆放。");
            return false;
        }
    }

    /// <summary>会议界面已经在屏幕上: 收起本机新身体的投票格、整理代坐界面、对齐进度。</summary>
    private static void AfterShown(MeetingHud meeting, string how)
    {
        // 本机这具身体是会议开始之后才有的, 别人的会议里没有它这一格 → 本机也收起来,
        //   免得出现"两个自己"(重连者)或给新进来的观战者一个无效的投票格。
        try
        {
            var lp = PlayerControl.LocalPlayer;
            var states = meeting.playerStates;
            if (lp != null && states != null)
            {
                foreach (var area in states)
                {
                    if (area != null && area.PlayerId.Value == lp.PlayerId
                        && !(MeetingSeat.Active && MeetingSeat.OldPlayerId == lp.PlayerId)
                        && !SpectatorSeat.AmSpectator)   // 观战者保留自己的死亡名牌(全场都会补上)
                    {
                        area.gameObject.SetActive(false);
                    }
                }
            }
        }
        catch { }

        // 投票格重建过: 代坐的界面整理(旧座位标"(你)")按新的投票格再做一次。
        try { MeetingSeat.InvalidateUi(); } catch { }

        // 开会动画里 NoS 会再确认一遍生死; 已经投过的票这里再补一次, 保证一张不丢。
        RestoreVotes(meeting, "显示后");

        try
        {
            if (MeetingFlag.TryGetHostMeeting(out var hostState, out var discussion, out var voting))
            {
                if (hostState >= (int)MeetingHud.MeetingStates.Results)
                {
                    NebulaResumeEntry.Log("会议对齐(" + how + "): 房主已在出结果, 本机只观看。");
                }
                else
                {
                    // 讨论中 → 剩余讨论时间; 投票中 → 讨论时间清零, 下一帧自动进入投票并开放按钮。
                    var inVoting = hostState >= (int)MeetingHud.MeetingStates.NotVoted;
                    MeetingFlag.SetModTimer("DiscussionTimer", inVoting ? 0f : discussion);
                    MeetingFlag.SetModTimer("VotingTimer", System.Math.Max(1f, voting));
                    meeting.state = MeetingHud.MeetingStates.Discussion;

                    NebulaResumeEntry.Log("会议对齐(" + how + "): 按房主进度 → " + (inVoting ? "投票中" : "讨论中")
                        + " 讨论剩 " + discussion.ToString("F1") + " 秒, 投票剩 " + voting.ToString("F1") + " 秒。");
                }
            }
            else
            {
                // 房主没广播(旧版房主): 用本机的默认倒计时直接进入讨论。
                if (meeting.state == MeetingHud.MeetingStates.Animating)
                {
                    meeting.state = MeetingHud.MeetingStates.Discussion;
                }

                NebulaResumeEntry.Log("会议对齐(" + how + "): 没有房主进度 → 按默认倒计时进入讨论。");
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议对齐异常: " + e.Message);
        }

        _diagMeeting = meeting;
        _diagAt = UnityEngine.Time.unscaledTime + 1.5f;
    }

    // ── 已投的票: 重建投票格前记下, 重建后原样放回 ──
    //   投票本身一直在房主那里算(房主的计时和票都不受本机重建影响);
    //   这里只是让本机看到的投票标记和房主一致, 并让已经投过票的旧座位不再能投。
    private static readonly Dictionary<byte, byte> _savedVotes = new();

    private static void SaveVotes(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<PlayerVoteArea>? states)
    {
        _savedVotes.Clear();
        if (states == null)
        {
            return;
        }

        try
        {
            foreach (var area in states)
            {
                if (area != null && area.DidVote)
                {
                    _savedVotes[(byte)area.PlayerId.Value] = (byte)area.VotedForId;
                }
            }
        }
        catch { }

        if (_savedVotes.Count > 0)
        {
            NebulaResumeEntry.Log("会议延迟搭建: 记下已投票 " + _savedVotes.Count + " 人(重建后放回)。");
        }
    }

    private static void RestoreVotes(MeetingHud meeting, string when)
    {
        if (_savedVotes.Count == 0)
        {
            return;
        }

        var restored = 0;
        try
        {
            var states = meeting.playerStates;
            if (states == null)
            {
                return;
            }

            foreach (var area in states)
            {
                if (area == null || area.DidVote || !_savedVotes.TryGetValue((byte)area.PlayerId.Value, out var votedFor))
                {
                    continue;
                }

                try { area.SetVote(votedFor); }
                catch
                {
                    area.VotedForId = votedFor;
                    area.DidVote = true;
                }

                restored++;
            }
        }
        catch { }

        if (restored > 0)
        {
            NebulaResumeEntry.Log("会议延迟搭建: 已放回 " + restored + " 张已投的票(" + when + ")。");
        }
    }

    /// <summary>代坐的旧座位已经投过票(房主那里算数): 本机进入"已投票", 不再显示投票按钮。</summary>
    internal static void LockIfSeatVoted(MeetingHud meeting)
    {
        if (!MeetingSeat.Active || meeting.state != MeetingHud.MeetingStates.NotVoted)
        {
            return;
        }

        var states = meeting.playerStates;
        if (states == null)
        {
            return;
        }

        foreach (var area in states)
        {
            if (area != null && area.PlayerId.Value == MeetingSeat.OldPlayerId && area.DidVote)
            {
                meeting.state = MeetingHud.MeetingStates.Voted;
                try { meeting.SkipVoteButton.ClearButtons(); } catch { }
                foreach (var other in states)
                {
                    try { other?.ClearButtons(); } catch { }
                }

                NebulaResumeEntry.Log("会议对齐: 旧座位在掉线前/房主那里已投过票 → 本机按已投票处理。");
                return;
            }
        }
    }

    // ── 诊断: 会议界面摆好 1.5 秒后, 记录一次投票格的实际状态(排查"框里什么都没有") ──
    private static MeetingHud? _diagMeeting;
    private static float _diagAt;

    private static void TickDiag()
    {
        var meeting = _diagMeeting;
        if (meeting == null || UnityEngine.Time.unscaledTime < _diagAt)
        {
            return;
        }

        _diagMeeting = null;

        try
        {
            if (MeetingHud.Instance == null || MeetingHud.Instance.Pointer != meeting.Pointer)
            {
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("会议诊断: 状态=").Append(meeting.state)
              .Append(" 会议active=").Append(meeting.gameObject.activeInHierarchy)
              .Append(" 父=").Append(meeting.transform.parent != null ? meeting.transform.parent.name : "无")
              .Append(" 位置=").Append(meeting.transform.localPosition.ToString())
              .Append(" 内容active=").Append(meeting.meetingContents.gameObject.activeInHierarchy)
              .Append(" 内容位置=").Append(meeting.meetingContents.localPosition.ToString());
            try { sb.Append(" 介绍层active=").Append(meeting.MeetingIntro.gameObject.activeInHierarchy); } catch { }

            var states = meeting.playerStates;
            sb.Append(" 投票格=").Append(states != null ? states.Length : -1);
            if (states != null)
            {
                foreach (var a in states)
                {
                    if (a == null)
                    {
                        sb.Append(" [空]");
                        continue;
                    }

                    var r = a.Background;
                    sb.Append(" [id=").Append(a.PlayerId.Value)
                      .Append(" act=").Append(a.gameObject.activeInHierarchy)
                      .Append(" 父=").Append(a.transform.parent != null ? a.transform.parent.name : "无")
                      .Append(" 位=").Append(a.transform.localPosition.ToString())
                      .Append(" 缩=").Append(a.transform.lossyScale.x.ToString("F2"))
                      .Append(" 底图=").Append(r != null && r.enabled && r.sprite != null)
                      .Append(" 透明=").Append(r != null ? r.color.a.ToString("F2") : "-")
                      .Append(" 遮罩=").Append(r != null ? r.maskInteraction.ToString() : "-")
                      .Append(" 死=").Append(a.AmDead)
                      .Append(']');
                }
            }

            NebulaResumeEntry.Log(sb.ToString());
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议诊断异常: " + e.Message);
        }
    }

    /// <summary>
    ///     把会议界面摆到屏幕上。
    /// </summary>
    /// <remarks>
    ///     平时这一步由开会动画完成: 把会议界面挂到 HUD 下、放到屏幕中央、把内容滑进画面、
    ///     关掉游戏内 HUD。中途进来的人没有开会动画, 会议界面就停在地图原点(镜头外),
    ///     看起来就是"会议界面没出来"。这里不播动画, 直接摆到动画结束时的样子。
    /// </remarks>
    private static void Present(MeetingHud meeting)
    {
        var hud = HudManager.Instance;
        if (hud == null)
        {
            return;
        }

        try { meeting.SkipVoteButton.SetDisabled(); } catch { }

        try
        {
            meeting.transform.SetParent(hud.transform);
            // 模组的开会补丁在 Start 后把会议界面放到 z=-25(盖在 HUD 之上), 这里保持一致。
            meeting.transform.localPosition = new UnityEngine.Vector3(0f, 0f, -25f);
            meeting.meetingContents.localPosition = UnityEngine.Vector3.zero;
            meeting.gameObject.SetActive(true);
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议显示: 摆放失败: " + e.Message);
        }

        // 开会动画的介绍层(报告人/尸体卡片、"守护天使最近保护了某个人")平时在动画末尾收起;
        //   中途进来没播动画, 它会一直盖在投票格上面 → 直接收起。
        try
        {
            var intro = meeting.MeetingIntro;
            if (intro != null)
            {
                try { intro.ProtectedRecently.SetActive(false); } catch { }
                intro.gameObject.SetActive(false);
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议显示: 收起介绍层失败: " + e.Message);
        }

        try
        {
            meeting.TitleText.text = DestroyableSingleton<TranslationController>.Instance.GetString(StringNames.MeetingWhoIsTitle);
        }
        catch { }

        try { hud.Chat.ForceClosed(); } catch { }
        try { hud.SetHudActive(false); } catch { }
        try { hud.MapButton.gameObject.SetActive(true); } catch { }
        try { meeting.SetMasksEnabled(false); } catch { }

        try
        {
            if (MapBehaviour.Instance != null)
            {
                MapBehaviour.Instance.Close();
            }

            if (Minigame.Instance != null)
            {
                Minigame.Instance.ForceClose();
            }
        }
        catch { }

        try
        {
            ControllerManager.Instance.OpenOverlayMenu(meeting.name, null, meeting.DefaultButtonSelected, meeting.ControllerSelectable, false);
        }
        catch { }

        NebulaResumeEntry.Log("会议显示: 已把会议界面摆到屏幕上(中途进入没有开会动画)。");
    }

    /// <summary>本机是"中途进来、还没续局"的机器: 有地图, 但本机 NoS 还没开局。</summary>
    private static bool IsMidJoinMachine()
    {
        if (ResumeTransition.Done || ShipStatus.Instance == null)
        {
            return false;
        }

        var ngmType = ResumeTransition.Find("Nebula.Game.NebulaGameManager");
        var ngm = ngmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var state = ngm == null ? null : ngmType!.GetProperty("GameState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ngm);
        return ngm != null && state != null && state.ToString() == "NotStarted";
    }

    private static bool LocalReady(out string why) => ResumeTransition.LocalBodyReady(out why);

    private static void KickCosmetics()
    {
        _cosmeticsKickAt = UnityEngine.Time.unscaledTime + 2f;
        try
        {
            var ship = ShipStatus.Instance;
            var cache = ship != null ? ship.CosmeticsCache : null;
            if (cache != null)
            {
                ship!.StartCoroutine(cache.PopulateFromPlayers());
            }
        }
        catch { }
    }

    /// <summary>全场玩家的名牌都能从装扮缓存里取到(投票格第一步就取它)。</summary>
    private static bool CosmeticsReady()
    {
        try
        {
            var ship = ShipStatus.Instance;
            var cache = ship != null ? ship.CosmeticsCache : null;
            var gd = GameData.Instance;
            if (cache == null || gd == null || gd.AllPlayers == null)
            {
                return false;
            }

            var all = gd.AllPlayers;
            for (var i = 0; i < all.Count; i++)
            {
                var info = all[i];
                if (info == null || info.Disconnected)
                {
                    continue;
                }

                var outfit = info.DefaultOutfit;
                if (outfit == null)
                {
                    return false;
                }

                if (string.IsNullOrEmpty(outfit.NamePlateId))
                {
                    continue;
                }

                if (cache.GetNameplate(outfit.NamePlateId) == null)
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
