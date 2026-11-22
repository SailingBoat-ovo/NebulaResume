// ============================================================
// MeetingSeat.cs — 会议中进入: 以旧身份的座位参加这场会议
//
// ▸ 场景
//   重连者在会议进行中回到房间。服务端(房主声明支持时)放行, 但把续局推迟到散会:
//   会议期间旧身份没有被移除, 它在所有人的会议界面上仍然有一个有效的投票格。
//   服务端发来 kind=13 [13, 旧PlayerId], 告诉本机"这场会议请坐旧身份的座位"。
//
// ▸ 做法
//   · 投票: 模组的投票是 ModCastVote(投票人, 目标, 票数), 房主按投票人找投票格计票。
//     本机点投票时, 把投票人从【新身份】换成【旧身份】—— 界面就是正常的会议界面。
//   · 投票权: 模组用掩码记录"谁能投票", 开会那一刻新身份还不存在, 不在掩码里;
//     旧身份能投票时, 给新身份也补上这一位, 本机的投票按钮才可用。
//   · 界面: 隐藏新身份那一格(避免出现两个自己), 在旧身份那一格名字后标注"(你)"。
//   · 散会后清空, 由服务端发来的续局信号走正常续局。
//
// ▸ 兼容
//   房主没声明支持时服务端不会发 kind=13, 本文件什么都不做。
//   只认服务端发给本机的帧; 与其它服务器、其它插件互不干扰。
// ============================================================

using System.Reflection;
using HarmonyLib;

namespace NebulaResume;

internal static class MeetingSeat
{
    internal const int KindMeetingSeat = 13;

    /// <summary>这场会议本机代坐的旧身份编号; -1 = 不在代坐。</summary>
    private static int _oldPlayerId = -1;

    private static float _receivedAt;
    private static bool _uiFixedThisMeeting;
    private static FieldInfo? _canVoteMaskField;

    internal static bool Active => _oldPlayerId >= 0;

    internal static int OldPlayerId => _oldPlayerId;

    // ── 本场会议被放逐 ──
    //   服务端的状态包是掉线那一刻采集的("还活着"), 散会后的正式续局会按它复活本机。
    //   代坐期间被投出去的话, 必须按灵魂恢复。会议结束时 Reset() 会清掉代坐信息, 所以单独记。
    private static float _exiledAt = -1000f;

    internal static void NoteExiled()
    {
        if (UnityEngine.Time.unscaledTime - _exiledAt > 30f)
        {
            NebulaResumeEntry.Log("会议中进入: 本机代坐的旧座位 " + _oldPlayerId + " 在本场会议被放逐, 续局时按灵魂恢复。");
        }

        _exiledAt = UnityEngine.Time.unscaledTime;
    }

    /// <summary>续局时调用: 最近一场会议里是否被放逐(读一次即清除)。</summary>
    internal static bool ConsumeExiled()
    {
        var exiled = UnityEngine.Time.unscaledTime - _exiledAt < 180f;
        _exiledAt = -1000f;
        return exiled;
    }

    /// <summary>投票格被重建过(会议延迟搭建): 界面整理要按新的投票格重做一遍。</summary>
    internal static void InvalidateUi() => _uiFixedThisMeeting = false;

    internal static void Reset()
    {
        _oldPlayerId = -1;
        _uiFixedThisMeeting = false;
    }

    /// <summary>收到服务端的座位帧(幂等: 服务端会周期重发防丢包)。</summary>
    internal static void OnReceived(int[] frame)
    {
        if (frame == null || frame.Length < 2)
        {
            return;
        }

        var old = frame[1];
        if (old < 0 || old > 254)
        {
            return;
        }

        if (_oldPlayerId != old)
        {
            NebulaResumeEntry.Log("会议中进入: 本场会议以旧身份 playerId=" + old + " 的座位投票。");
        }

        _oldPlayerId = old;
        _receivedAt = UnityEngine.Time.unscaledTime;
    }

    /// <summary>由 Entry 每帧调用: 补投票权、整理界面; 散会后清空。</summary>
    internal static void Tick()
    {
        if (!Active)
        {
            return;
        }

        var meeting = MeetingHud.Instance;
        if (meeting == null)
        {
            // 会议已结束(或还没搭起来)。服务端每 3 秒重发一次: 超过 8 秒没收到就视为散会。
            if (UnityEngine.Time.unscaledTime - _receivedAt > 8f)
            {
                NebulaResumeEntry.Log("会议中进入: 会议结束, 等待正式续局。");
                Reset();
            }

            return;
        }

        try { GrantVoteRight(); } catch { }

        if (!_uiFixedThisMeeting)
        {
            try { _uiFixedThisMeeting = FixUi(meeting); } catch { }
        }
    }

    /// <summary>旧身份能投票时, 给本机新身份也补上投票权位。</summary>
    private static void GrantVoteRight()
    {
        var lp = PlayerControl.LocalPlayer;
        if (lp == null)
        {
            return;
        }

        _canVoteMaskField ??= ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension")
            ?.GetField("CanVoteMask", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (_canVoteMaskField == null)
        {
            return;
        }

        var mask = (int)_canVoteMaskField.GetValue(null)!;
        var oldBit = 1 << _oldPlayerId;
        var newBit = 1 << lp.PlayerId;
        if ((mask & oldBit) != 0 && (mask & newBit) == 0)
        {
            _canVoteMaskField.SetValue(null, mask | newBit);
        }
    }

    /// <summary>隐藏新身份的投票格, 标注旧身份那一格。返回 true 表示已处理完。</summary>
    private static bool FixUi(MeetingHud meeting)
    {
        var lp = PlayerControl.LocalPlayer;
        var states = meeting.playerStates;
        if (lp == null || states == null || states.Length == 0)
        {
            return false;
        }

        var foundOld = false;
        foreach (var area in states)
        {
            if (area == null)
            {
                continue;
            }

            var id = area.PlayerId.Value;
            if (id == lp.PlayerId && id != _oldPlayerId)
            {
                area.gameObject.SetActive(false);
            }
            else if (id == _oldPlayerId)
            {
                foundOld = true;
                var label = area.NameText;
                if (label != null && !label.text.EndsWith("(你)"))
                {
                    label.text += " (你)";
                }
            }
        }

        return foundOld;
    }

    // ── 投票人替换 ──

    /// <summary>由 Entry 安装: 拦截模组的 ModCastVote。</summary>
    internal static void Install(Harmony harmony)
    {
        try
        {
            var t = ResumeTransition.Find("Nebula.Extensions.MeetingHudExtension");
            var m = t?.GetMethod("ModCastVote", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null)
            {
                NebulaResumeEntry.Log("会议中进入: 未找到 ModCastVote, 代坐投票不可用。");
                return;
            }

            harmony.Patch(m, prefix: new HarmonyMethod(typeof(MeetingSeat).GetMethod(nameof(ModCastVotePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            NebulaResumeEntry.Log("会议中进入: 已挂代坐投票。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("会议中进入: 挂载代坐投票失败: " + e.Message);
        }
    }

    /// <summary>
    ///     ModCastVote(this MeetingHud meeting, byte playerId, byte suspectIdx, int votes):
    ///     代坐期间, 本机以新身份投出的票改记到旧身份名下。
    /// </summary>
    private static void ModCastVotePrefix([HarmonyArgument(1)] ref byte playerId, [HarmonyArgument(2)] byte suspectIdx, [HarmonyArgument(3)] int votes)
    {
        try
        {
            var lp = PlayerControl.LocalPlayer;
            if (!Active || lp == null || playerId != lp.PlayerId || MeetingHud.Instance == null)
            {
                return;
            }

            NebulaResumeEntry.Log("会议中进入: 投票人 " + playerId + " → 旧身份 " + _oldPlayerId + "(目标 " + suspectIdx + ")。");
            playerId = (byte)_oldPlayerId;

            // 同时走插件自己的通道把这一票交给房主(房主那边幂等, 已投过的不会重复计),
            // 并让本机界面立刻进入"已投票"。
            MeetingProxy.SendSeatVote(_oldPlayerId, suspectIdx, votes);
        }
        catch { }
    }
}
