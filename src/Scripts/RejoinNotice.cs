// ============================================================
// RejoinNotice.cs — 重连回来时的两件小事
//
// ① 散会后的位置(RejoinPlacement)
//   原版: 开会时所有人被传到会议桌, 散会后从会议桌出发。
//   规则(由服务端在续局信号 kind=3 的第 3 个值里告诉本机"掉线后有没有开过会"):
//     · 掉线后没开过会      → 恢复到掉线时的位置(原有行为)。
//     · 掉线后开过会        → 不回原处, 放到会议桌(散会后的默认位置):
//         - 会议中回来的: 本机已经亲历散会。地图需要选出生点(飞艇 / NOS 出生点选择)
//           时玩家已经自己选过, 保持不动; 否则已在会议桌附近也保持不动, 不在就放到会议桌。
//         - 会议已经结束才回来的: 放到会议桌。
//     · 旧服务端不带这个值   → 按"没开过会"处理, 与以前一致。
//
// ② 重连横幅(RejoinBanner)
//   服务端向全场广播 kind=16 [16, playerId, 名字长度, 名字..., 好友代码长度, 好友代码...],
//   每个人屏幕正中偏上显示"名字 (好友代码) 重连回本局游戏。", 2.5 秒后直接消失(普通白字, 不渐变)。
//   会议中回来的人, 服务端在他进会议时就广播, 不等散会。
//   复制任务栏的文字组件来显示, 字体与原版一致(中文正常显示), 不弹窗、不挡操作。
// ============================================================

using UnityEngine;
using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;
using Color = UnityEngine.Color;
using Object = UnityEngine.Object;

namespace NebulaResume;

internal static class RejoinPlacement
{
    /// <summary>服务端说: 掉线之后开过会(含掉线时正在开会)。</summary>
    internal static bool MeetingSinceDisconnect;

    /// <summary>本机最近一次看到会议界面 / 放逐画面的时刻。</summary>
    private static float _lastMeetingSeenAt = -1000f;

    /// <summary>由 Entry.Tick 每帧调用: 记下本机是否亲历了会议。</summary>
    internal static void Tick()
    {
        try
        {
            if (MeetingHud.Instance != null || ExileController.Instance != null)
            {
                _lastMeetingSeenAt = Time.unscaledTime;
            }
        }
        catch { }
    }

    /// <summary>
    ///     续局时决定位置。返回 true = 照旧恢复掉线位置(snap 不变);
    ///     返回 false 且 snap.HasPosition = true = 改为瞬移到 snap 里的会议桌位置;
    ///     返回 false 且 snap.HasPosition = false = 保持当前位置不动。
    /// </summary>
    internal static bool Decide(ref NebulaStateCodec.Snapshot snap)
    {
        if (!MeetingSinceDisconnect)
        {
            return true;
        }

        var lp = PlayerControl.LocalPlayer;
        var ship = ShipStatus.Instance;
        if (lp == null || ship == null)
        {
            snap.HasPosition = false;
            NebulaResumeEntry.Log("位置: 掉线后开过会, 但地图/本机角色不可用 → 不改位置。");
            return false;
        }

        var sawMeetingEnd = Time.unscaledTime - _lastMeetingSeenAt < 45f;
        if (sawMeetingEnd && MapChoosesSpawn(ship))
        {
            snap.HasPosition = false;
            NebulaResumeEntry.Log("位置: 本机在会议中回来, 散会时已自己选过出生点 → 保持当前位置。");
            return false;
        }

        Vector2 cur = lp.transform.position;
        if (NearTable(ship, cur))
        {
            snap.HasPosition = false;
            NebulaResumeEntry.Log("位置: 掉线后开过会, 本机已在会议桌附近 → 保持当前位置。");
            return false;
        }

        var spot = TableSpot(ship, lp);
        snap.PosX = Mathf.RoundToInt(spot.x * 1000f);
        snap.PosY = Mathf.RoundToInt(spot.y * 1000f);
        snap.HasPosition = true;
        NebulaResumeEntry.Log("位置: 掉线后开过会(" + (sawMeetingEnd ? "会议中回来" : "散会后才回来")
            + ") → 放到会议桌 (" + spot.x.ToString("F2") + ", " + spot.y.ToString("F2") + "), 不回掉线处。");
        return false;
    }

    /// <summary>散会后需要玩家自己选出生点的地图: 飞艇, 或开了 NOS 出生点选择。</summary>
    private static bool MapChoosesSpawn(ShipStatus ship)
    {
        try
        {
            if (ship.TryCast<AirshipStatus>() != null)
            {
                return true;
            }
        }
        catch { }

        try
        {
            var t = ResumeTransition.Find("Nebula.Behavior.NebulaPreSpawnMinigame");
            var p = t?.GetProperty("PreSpawnLocations", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (p?.GetValue(null) is System.Array arr && arr.Length > 0)
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool NearTable(ShipStatus ship, Vector2 cur)
    {
        var r = ship.SpawnRadius + 1.5f;
        if (Vector2.Distance(cur, ship.MeetingSpawnCenter) <= r)
        {
            return true;
        }

        var c2 = ship.MeetingSpawnCenter2;
        return c2 != Vector2.zero && Vector2.Distance(cur, c2) <= r;
    }

    /// <summary>与原版散会落位相同的算法: 以会议桌为圆心, 按 PlayerId 均分一圈。</summary>
    private static Vector2 TableSpot(ShipStatus ship, PlayerControl lp)
    {
        var n = 10;
        try
        {
            if (GameData.Instance != null && GameData.Instance.PlayerCount > 0)
            {
                n = GameData.Instance.PlayerCount;
            }
        }
        catch { }

        var angle = (lp.PlayerId - 1) * (360f / n) * Mathf.Deg2Rad;
        var dir = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle));
        return ship.MeetingSpawnCenter + dir * ship.SpawnRadius + new Vector2(0f, 0.3636f);
    }
}

internal static class RejoinBanner
{
    internal const int Kind = 16;

    /// <summary>显示时长(秒): 一眼能读完, 又不久留。到点直接消失, 不做渐变。</summary>
    private const float Duration = 2.5f;

    private static TMPro.TextMeshPro? _text;
    private static float _shownAt = -100f;
    private static readonly Dictionary<int, float> _recent = new();

    /// <summary>收到服务端广播的 kind=16。</summary>
    internal static void OnFrame(int[] frame)
    {
        try
        {
            if (frame == null || frame.Length < 4)
            {
                return;
            }

            var playerId = frame[1];
            var i = 2;
            var name = ReadString(frame, ref i);
            var code = ReadString(frame, ref i);

            // 服务端可能因重试发两次: 同一个人 10 秒内只显示一次。
            var now = Time.unscaledTime;
            if (_recent.TryGetValue(playerId, out var last) && now - last < 10f)
            {
                return;
            }

            _recent[playerId] = now;

            // 优先用游戏里显示的名字(服务端记的是连接时的名字, 可能与游戏内不同)。
            try
            {
                var gameName = GameData.Instance?.GetPlayerById((byte)playerId)?.PlayerName;
                if (!string.IsNullOrEmpty(gameName))
                {
                    name = gameName;
                }
            }
            catch { }

            var who = Escape(string.IsNullOrEmpty(name) ? ("玩家 " + playerId) : name);
            if (!string.IsNullOrEmpty(code))
            {
                who += " (" + Escape(code) + ")";
            }

            NebulaResumeEntry.Log("重连横幅: " + who + " 重连回本局游戏。");
            Show(who + " 重连回本局游戏。");
        }
        catch (System.Exception e)
        {
            NebulaResumeEntry.Log("重连横幅异常: " + e.Message);
        }
    }

    private static string ReadString(int[] frame, ref int i)
    {
        if (i >= frame.Length)
        {
            return "";
        }

        var len = frame[i++];
        if (len <= 0 || len > 64 || i + len > frame.Length)
        {
            return "";
        }

        var chars = new char[len];
        for (var k = 0; k < len; k++)
        {
            chars[k] = (char)frame[i++];
        }

        return new string(chars);
    }

    /// <summary>名字里的尖括号会被当成富文本标签, 换成全角。</summary>
    private static string Escape(string s) => s.Replace("<", "＜").Replace(">", "＞");

    private static void Show(string message)
    {
        var hud = HudManager.Instance;
        if (hud == null)
        {
            return;
        }

        if (_text == null)
        {
            var src = hud.TaskPanel != null ? hud.TaskPanel.taskText : null;
            if (src == null)
            {
                return;
            }

            _text = Object.Instantiate(src, hud.transform);
            _text.name = "NebulaResumeRejoinBanner";
            _text.alignment = TMPro.TextAlignmentOptions.Center;
            _text.enableWordWrapping = false;
            _text.richText = true;
            _text.fontSize = 2.2f;
            // 普通白字: 关掉从任务栏复制来的渐变色与样式。
            _text.enableVertexGradient = false;
            _text.fontStyle = TMPro.FontStyles.Normal;
            _text.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            _text.rectTransform.sizeDelta = new Vector2(14f, 1f);
            _text.transform.localScale = Vector3.one;
            // 正中偏上: 屏幕上沿往下一点, 避开左上任务栏与右上按钮。
            _text.transform.localPosition = new Vector3(0f, 1.95f, -400f);
        }

        _text.text = message;
        _text.color = Color.white;
        _text.alpha = 1f;
        _text.gameObject.SetActive(true);
        _shownAt = Time.unscaledTime;
    }

    /// <summary>由 Entry.Tick 每帧调用: 到点隐藏。</summary>
    internal static void Tick()
    {
        try
        {
            if (_text == null || !_text.gameObject.activeSelf)
            {
                return;
            }

            if (Time.unscaledTime - _shownAt >= Duration)
            {
                _text.gameObject.SetActive(false);
            }
        }
        catch { }
    }
}
