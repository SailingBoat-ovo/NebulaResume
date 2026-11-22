// ============================================================
// ReconnectPrompt.cs — 掉线提示  v27
//
// ── 这一版做了什么 ─────────────────────────────────────────
//   · 游戏自己的掉线报错(超时 / 被踢 / 服务器关闭 ...)原样显示, 不再被替换。
//   · 非主动的断开: 在报错下方追加一段重连说明, 带上刚才的房间号,
//     并写明只有帆船服务端(Fmpostor)的服务器才支持。
//   · 游戏没有弹窗时(例如本插件主动退出), 才单独弹出这段说明。
//
// ── 设计原则 ───────────────────────────────────────────────
//   · 只用游戏自带的 UI(DisconnectPopup), 不自绘任何窗口
//   · 游戏原有弹窗的内容只追加、不删改
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
        // 只在游戏弹窗显示/改字之后补一段说明, 不改它原来的内容与逻辑。
        InstallPopupHooks(harmony);
        NebulaResumeEntry.Log("掉线提示: 保留游戏原本的报错, 重连说明追加在下方。");
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

            TickHint();

            if (inRoom)
            {
                RememberRoomCode(client);
                _pendingHint = null;

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

            // 玩家自己点了退出(ExitGame 被调用, 且不是本插件发起的):
            //   游戏的 LastDisconnectReason 在主动退出时不一定会更新(可能还是上一次的值),
            //   所以以"刚刚调用过退出"为准。服务端对主动退出也不保留位置, 不能再提示"可以重连"。
            if (now - _manualExitAt < ManualExitWindowSeconds)
            {
                voluntary = true;
            }

            NebulaResumeEntry.Log("离开房间: 原因=" + reason + (voluntary ? "(主动退出, 不提示重连)" : "(非主动断开)") + "。");

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

    /// <summary>回主菜单后的"重连失败"提示。</summary>
    private static void ShowFailedNotice(bool byMeeting)
    {
        var message = byMeeting
            ? Text("prompt.meetingRefused",
                "【断线重连】会议进行中, 本次重连已取消。请等会议结束后重新连接这台服务器并输入房间号 %CODE%。"
                + "你的位置依然保留, 本次不计入重连次数。")
            : Text("prompt.reconnectFailed",
                "【断线重连】这次重连没有成功(可能是会议正在进行或网络问题)。你的位置依然保留, "
                + "稍后重新连接这台服务器并输入房间号 %CODE% 再试一次, 失败不计入重连次数。"
                + "注意: 只有使用帆船服务端(Fmpostor)的服务器才支持断线重连。");
        QueueHint(message, "重连失败提示");
    }

    /// <summary>普通的意外掉线: 在游戏原本的报错下面补一段"可以重连"的说明。</summary>
    private static void ShowNativeNotice()
    {
        // 房主关掉了断线重连: 不提示"可以重连", 游戏原本的掉线弹窗原样保留。
        var allowed = true;
        try { allowed = ResumeOptions.ReconnectAllowed; } catch { }
        if (!allowed)
        {
            NebulaResumeEntry.Log("掉线提示: 本房间已关闭断线重连, 不显示重连说明。");
            return;
        }

        QueueHint(Text("prompt.disconnected",
            "【断线重连】这局还能回去: 重新连接这台服务器, 输入房间号 %CODE% 即可回到原来的对局, 职业和进度都会保留。"
            + "注意: 只有使用帆船服务端(Fmpostor)的服务器才支持断线重连, 其他服务器请忽略本条。"), "掉线提示");
    }

    // ── 附加说明: 不替换游戏的报错, 只在它下面追加 ──
    //
    //   游戏自己的掉线弹窗(原因: 超时 / 被踢 / 服务器关闭 ...)照常显示,
    //   本插件只在原文末尾空一行补上重连说明和房间号。
    //   弹窗还没出来时先挂起, 弹窗出现(DoShow / SetText / ShowCustom 之后)再补;
    //   等一会儿游戏都没弹窗(例如本插件自己主动退出的情况), 才单独弹出说明。

    /// <summary>最近一次在房间里看到的房间号。</summary>
    private static string _lastRoomCode = "";

    private static string? _pendingHint;
    private static string _pendingTag = "";
    private static float _pendingAt;
    private static bool _appending;
    private static bool _standaloneShown;
    private static bool _appendedOnce;

    /// <summary>等游戏弹窗出现的时间(秒); 超过还没有就单独弹出说明。</summary>
    private const float HintStandaloneAfter = 2f;

    /// <summary>挂起的说明最多保留多久(秒)。</summary>
    private const float HintKeepSeconds = 20f;

    private static string Text(string key, string fallback)
    {
        string message;
        try { message = NebulaAPI.Language.Translate(key); } catch { message = ""; }
        if (string.IsNullOrEmpty(message) || message.StartsWith("prompt.") || message.StartsWith("*"))
        {
            message = fallback;
        }

        var code = string.IsNullOrEmpty(_lastRoomCode) ? "?" : _lastRoomCode;
        return message.Replace("%CODE%", code);
    }

    private static void RememberRoomCode(AmongUsClient client)
    {
        try
        {
            var id = client.GameId;
            if (id != 0 && id != 32)
            {
                var code = InnerNet.GameCode.IntToGameName(id);
                if (!string.IsNullOrEmpty(code))
                {
                    _lastRoomCode = code;
                }
            }
        }
        catch { }
    }

    private static void QueueHint(string message, string tag)
    {
        _pendingHint = message;
        _pendingTag = tag;
        _pendingAt = UnityEngine.Time.unscaledTime;
        _standaloneShown = false;
        _appendedOnce = false;
        NebulaResumeEntry.Log(tag + ": 等游戏的掉线弹窗出现后追加重连说明(房间号 " + _lastRoomCode + ")。");
        TryAppend();
    }

    /// <summary>每帧: 弹窗出现就追加; 迟迟没有弹窗就单独弹出。</summary>
    private static void TickHint()
    {
        if (_pendingHint == null)
        {
            return;
        }

        var age = UnityEngine.Time.unscaledTime - _pendingAt;
        if (age > HintKeepSeconds)
        {
            _pendingHint = null;
            return;
        }

        if (TryAppend())
        {
            _appendedOnce = true;
            return;
        }

        // 已经追加到游戏的弹窗上过(玩家看到并关掉了): 不再单独弹第二次。
        //   之前这里会在弹窗关掉 2 秒后又单独弹一遍, 就是"被踢时提示重复"。
        //   说明继续挂着(到期自动作废): 游戏若换场景后重新弹出同一个报错, 仍会再追加到它上面。
        if (_appendedOnce)
        {
            return;
        }

        if (_standaloneShown || age < HintStandaloneAfter)
        {
            return;
        }

        try
        {
            if (DestroyableSingleton<DisconnectPopup>.InstanceExists)
            {
                _standaloneShown = true;
                _appending = true;
                try { DestroyableSingleton<DisconnectPopup>.Instance.ShowCustom(_pendingHint); }
                finally { _appending = false; }

                // 说明继续挂着: 之后若游戏自己的弹窗盖上来, 仍会再追加一次。
                NebulaResumeEntry.Log(_pendingTag + ": 游戏没有弹出掉线窗口, 单独显示重连说明。");
            }
        }
        catch { }
    }

    /// <summary>弹窗开着且还没带说明 → 在原文后面追加。返回 true 表示说明已在弹窗上。</summary>
    private static bool TryAppend()
    {
        var hint = _pendingHint;
        if (hint == null || _appending)
        {
            return false;
        }

        try
        {
            if (!DestroyableSingleton<DisconnectPopup>.InstanceExists)
            {
                return false;
            }

            var popup = DestroyableSingleton<DisconnectPopup>.Instance;
            if (popup == null || !popup.gameObject.activeInHierarchy)
            {
                return false;
            }

            var area = popup._textArea;
            if (area == null)
            {
                return false;
            }

            var current = area.text ?? "";
            if (current.Contains(hint))
            {
                return true;
            }

            var merged = string.IsNullOrWhiteSpace(current) ? hint : current + "\n\n" + hint;
            _appending = true;
            try { area.text = merged; }
            finally { _appending = false; }

            NebulaResumeEntry.Log(_pendingTag + ": 已在游戏原本的报错下方追加重连说明。");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void PopupPostfix()
    {
        try { TryAppend(); } catch { }
    }

    // ── 手动退出 ──
    private static float _manualExitAt = -1000f;
    private const float ManualExitWindowSeconds = 15f;

    /// <summary>本插件自己调用退出时置位, 不当成玩家手动退出。</summary>
    internal static bool PluginExiting;

    private static void ExitGamePrefix()
    {
        try
        {
            if (PluginExiting)
            {
                return;
            }

            _manualExitAt = UnityEngine.Time.unscaledTime;
            _pendingHint = null;   // 退出前若有挂起的说明, 一并作废
        }
        catch { }
    }

    private static void InstallPopupHooks(Harmony harmony)
    {
        try
        {
            var exit = AccessTools.Method(typeof(AmongUsClient), nameof(AmongUsClient.ExitGame));
            if (exit != null)
            {
                harmony.Patch(exit, prefix: new HarmonyMethod(typeof(ReconnectPrompt), nameof(ExitGamePrefix)));
            }
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("掉线提示: 挂载 AmongUsClient.ExitGame 失败: " + e.Message);
        }

        foreach (var name in new[] { "DoShow", "SetText", "ShowCustom" })
        {
            try
            {
                var m = AccessTools.Method(typeof(DisconnectPopup), name);
                if (m != null)
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(typeof(ReconnectPrompt), nameof(PopupPostfix)));
                }
            }
            catch (System.Exception e)
            {
                NebulaResumeEntry.Log("掉线提示: 挂载 DisconnectPopup." + name + " 失败: " + e.Message);
            }
        }
    }
}
