// ============================================================
// ReconnectPrompt.cs — 掉线提示  v27
//
// ── 这一版做了什么 ─────────────────────────────────────────
//   · 不再给原生掉线弹窗"追加文字"(该方式不稳定, 玩家经常看不到提示)
//   · 改为: 检测到非主动的断开 → 【调用游戏原生的掉线弹窗】显示一条明确的提示,
//     告诉玩家"连接意外中断, 重新进入本服务器即可回到这一局"。
//     不显示房间号 —— 玩家重新进服后房间仍在, 直接输入该房间号即可;
//     房间号在大厅界面上本来就一直可见。
//
// ── 设计原则 ───────────────────────────────────────────────
//   · 只用游戏自带的 UI(DisconnectPopup.ShowCustom), 不自绘任何窗口
//   · 不改游戏原有弹窗的内容与逻辑
//   · 提示只弹一次, 回到房间后自动复位
// ============================================================

using HarmonyLib;

namespace NebulaResume;

internal static class ReconnectPrompt
{
    /// <summary>上一帧是否处在房间/对局内 —— 用来识别"刚刚掉线"。</summary>
    private static bool _wasInRoom;

    /// <summary>本次断开是否已经提示过。</summary>
    private static bool _notified;

    internal static void Install(Harmony harmony)
    {
        // 不挂任何补丁: 提示改为在 Tick 里主动调用原生弹窗,
        // 避免与游戏自己的弹窗显示流程互相干扰。
        NebulaResumeEntry.Log("掉线提示: 使用游戏原生弹窗(不挂补丁)。");
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

            var inRoom = LobbyBehaviour.Instance != null
                         || ShipStatus.Instance != null
                         || client.GameState != InnerNetClient.GameStates.NotJoined;

            var now = UnityEngine.Time.unscaledTime;

            if (inRoom)
            {
                if (!_wasInRoom)
                {
                    _roomEnteredAt = now;
                    _succeededThisVisit = false;
                }

                _wasInRoom = true;
                _notified = false;

                // 回到房间 → 允许下一次"重连失败"再提示。
                _failedShown = false;

                // 记下"最近一次看到会议进行中"的时刻: MeetingFlag 的信息 3 秒就过期,
                // 等被踢回主菜单再查往往已经看不到了。
                try
                {
                    if (MeetingFlag.Active || ExileController.Instance != null)
                    {
                        _meetingSeenAt = now;
                    }
                }
                catch { }

                return;
            }

            // 已经回到主菜单: 如果这一次是"重连尝试失败", 优先进这个提示。
            if (_failed && !_failedShown)
            {
                ShowFailedOnce();
                return;
            }

            // 从"在房间里"变成"不在房间" → 刚刚断开。
            if (!_wasInRoom)
            {
                return;
            }

            _wasInRoom = false;

            var attemptOpen = _attemptOpen && now - _attemptAt < AttemptWindowSeconds;
            _attemptOpen = false;   // 尝试只属于这一次进房

            if (_notified)
            {
                return;
            }

            // 玩家自己主动退出游戏的(那不是掉线)。
            //   注: 本插件"会议中拒绝"也是用 ExitGame 退出的, 但那条路径已经提前置了 _failed。
            var reason = client.LastDisconnectReason.ToString();
            var voluntary = reason == "ExitGame" || reason == "IntentionalLeaving" || reason == "NewConnection";

            if (!voluntary)
            {
                // ── 判"重连失败"(而不是普通掉线) ──
                //
                //   ① 本次进房有一次【尚未完成】的重连/入场尝试(收到过 kind=3/kind=2 或
                //      已被判定为观战者, 但还没恢复成功)。成功之后的掉线不算 ——
                //      这正是早先"普通掉线弹重连失败"串台的原因。
                //   ② 刚进房不久(中途加入者)就在会议期间被移出(典型: 房主踢出会议中接入的连接)。
                //      这种情况服务端可能根本没来得及发 kind=3, 只能靠时间判断。
                var meetingRecently = now - _meetingSeenAt < MeetingSeenWindowSeconds;
                var shortVisit = now - _roomEnteredAt < ShortVisitSeconds;

                //   ⚠️ ② 只在本次进房【没有成功恢复过】时才算: 已经回到对局的人,
                //      之后在会议里被踢/掉线是普通掉线, 不是"重连失败"。
                if (attemptOpen || (shortVisit && meetingRecently && !_succeededThisVisit))
                {
                    _notified = true;
                    _failed = true;
                    _failedByMeeting = meetingRecently;
                    NebulaResumeEntry.Log("重连失败: 本次进房的尝试未完成就被断开(原因=" + reason
                        + ", 会议中=" + meetingRecently + ")。");
                    ShowFailedOnce();
                    return;
                }
            }

            if (voluntary)
            {
                return;
            }

            _notified = true;
            ShowNativeNotice();
        }
        catch { }
    }

    /// <summary>本次进房是否已经出现过"重连失败"的迹象(被房主踢出/被拒绝)。</summary>
    private static bool _failed;

    /// <summary>失败是否因为会议进行中(决定用哪条文案)。</summary>
    private static bool _failedByMeeting;

    /// <summary>失败提示是否已经弹过(避免回主菜单后反复弹)。</summary>
    private static bool _failedShown;

    // ── 本次进房的重连/入场尝试 ──
    private static bool _attemptOpen;
    private static float _attemptAt;

    /// <summary>尝试的有效期(秒): 超过还没成功也没断开, 之后的掉线按普通掉线处理。</summary>
    private const float AttemptWindowSeconds = 90f;

    private static float _roomEnteredAt;
    private static float _meetingSeenAt = -1000f;

    /// <summary>"最近看到会议"的窗口(秒)。</summary>
    private const float MeetingSeenWindowSeconds = 6f;

    /// <summary>"刚进房不久"的窗口(秒)。</summary>
    private const float ShortVisitSeconds = 60f;

    /// <summary>
    ///     本次进房开始了一次重连/入场尝试(kind=3 / kind=2 / 观战判定)。
    ///     尝试完成前被断开 → 回主菜单按"重连失败"提示。
    /// </summary>
    internal static void NoteAttempt(string why)
    {
        if (!_attemptOpen)
        {
            NebulaResumeEntry.Log("重连尝试开始: " + why + "。");
        }

        _attemptOpen = true;
        _attemptAt = UnityEngine.Time.unscaledTime;
    }

    /// <summary>本次尝试已成功(状态已应用 / 观战席已就位)。</summary>
    internal static void NoteAttemptSucceeded()
    {
        if (_attemptOpen)
        {
            NebulaResumeEntry.Log("重连尝试完成: 之后的掉线按普通掉线提示。");
        }

        _attemptOpen = false;
        _succeededThisVisit = true;
    }

    /// <summary>本次进房是否已经成功恢复过(重连状态已应用 / 观战席已就位)。</summary>
    private static bool _succeededThisVisit;

    /// <summary>
    ///     标记"这一次重连失败了"。回到主菜单后提示。
    ///     调用点: 本机因会议进行中主动拒绝(byMeeting = true)。
    /// </summary>
    internal static void MarkReconnectFailed(string why, bool byMeeting = false)
    {
        _attemptOpen = false;

        if (_failed)
        {
            _failedByMeeting |= byMeeting;
            return;
        }

        _failed = true;
        _failedByMeeting = byMeeting;
        NebulaResumeEntry.Log("重连失败: " + why + " → 回到主菜单后提示玩家(不计入重连上限)。");
    }

    private static void ShowFailedOnce()
    {
        _failedShown = true;
        _failed = false;
        _wasInRoom = false;
        _notified = true;
        ShowFailedNotice(_failedByMeeting);
        _failedByMeeting = false;
    }

    /// <summary>回主菜单后弹"重连失败"提示(用游戏自带的掉线弹窗)。</summary>
    private static void ShowFailedNotice(bool byMeeting)
    {
        try
        {
            if (!DestroyableSingleton<DisconnectPopup>.InstanceExists)
            {
                NebulaResumeEntry.Log("重连失败提示: 原生弹窗实例不存在, 跳过。");
                return;
            }

            string message;

            if (byMeeting)
            {
                message = NebulaAPI.Language.Translate("prompt.meetingRefused");

                if (string.IsNullOrEmpty(message) || message.StartsWith("prompt.") || message.StartsWith("*"))
                {
                    message = "会议进行中, 本次重连已取消。请等会议结束后重新输入房间号进入。"
                              + "您的槽位依然保留, 本次不计入重连次数。";
                }
            }
            else
            {
                message = NebulaAPI.Language.Translate("prompt.reconnectFailed");

                if (string.IsNullOrEmpty(message) || message.StartsWith("prompt.") || message.StartsWith("*"))
                {
                    message = "重连失败，可能是会议正在进行或者其他网络原因。"
                              + "您的槽位依然保留，请稍后重试。"
                              + "重连失败不计入玩家重连上限！";
                }
            }

            DestroyableSingleton<DisconnectPopup>.Instance.ShowCustom(message);
            NebulaResumeEntry.Log("重连失败提示: 已在原生弹窗显示。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("重连失败提示: 调用原生弹窗失败: " + e.GetType().Name + " : " + e.Message);
        }
    }

    /// <summary>
    ///     调用游戏原生的掉线弹窗显示提示。
    ///     这是游戏自带的 UI, 一定存在、一定会显示。
    /// </summary>
    private static void ShowNativeNotice()
    {
        try
        {
            if (!DestroyableSingleton<DisconnectPopup>.InstanceExists)
            {
                NebulaResumeEntry.Log("掉线提示: 原生弹窗实例不存在, 跳过。");
                return;
            }

            // 走翻译表; 没装语言文件时回退到内置中文。
            var message = NebulaAPI.Language.Translate("prompt.disconnected");

            if (string.IsNullOrEmpty(message) || message.StartsWith("prompt.") || message.StartsWith("*"))
            {
                // 回退文案同样只写一句话: 语言文件的值不支持换行转义,
                // 两条路径的文案必须看起来一致, 否则"装了语言文件 / 没装"会显示成两种样子。
                message = "与服务器的连接意外中断, 重新进入本服务器即可回到这一局。";
            }

            DestroyableSingleton<DisconnectPopup>.Instance.ShowCustom(message);
            NebulaResumeEntry.Log("掉线提示: 已在原生弹窗显示重连说明。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("掉线提示: 调用原生弹窗失败: " + e.GetType().Name + " : " + e.Message);
        }
    }
}
