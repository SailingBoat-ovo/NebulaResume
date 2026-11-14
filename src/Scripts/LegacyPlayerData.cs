// ============================================================
// LegacyPlayerData.cs — 旧版玩家数据层的读写(反射)
//
// ▸ 为什么需要
//   NOS 的玩家状态有三套独立存储:
//     ① PlayerControl.Data.IsDead          (香草层)
//     ② PlayerModInfo.IsDead               (新版模组层)
//     ③ GameData.playersArray[id].IsAlive  (旧版数据层)  ← 本文件
//
//   ③ 决定【胜负判定 / 统计 / 死亡名单】。只写 ①② 不写 ③,
//   就会出现"香草层是活人、NOS 仍认为他死了"。
//
// ▸ 为什么用反射
//   GameData / PlayerData / PlayerStatus 在源码里是 public, 但没有暴露在
//   addon 的编译引用里 —— 直接引用会 CS0234。所以按类型名运行时查找。
//
// ▸ 对应源码
//     PlayerData.Revive(bool changeStatus = true)    GameData.cs:1127
//     PlayerData.Die(PlayerStatus, byte murderId)    GameData.cs:1135
//     PlayerData.Die()                               GameData.cs:1159
// ============================================================

using System.Reflection;

namespace NebulaResume;

internal static class LegacyPlayerData
{
    private static System.Type? _gameDataType;
    private static bool _typeSearched;

    private static System.Type? GameDataType()
    {
        if (_typeSearched)
        {
            return _gameDataType;
        }

        _typeSearched = true;

        try
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType("Nebula.Game.GameData", false);
                    if (t != null)
                    {
                        _gameDataType = t;
                        break;
                    }
                }
                catch { }
            }
        }
        catch { }

        return _gameDataType;
    }

    private static object? Data()
    {
        try
        {
            var t = GameDataType();
            return t?.GetField("data", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null);
        }
        catch
        {
            return null;
        }
    }

    private static object? PlayerDataOf(byte playerId)
    {
        try
        {
            var data = Data();
            if (data == null)
            {
                return null;
            }

            var arr = data.GetType()
                .GetField("playersArray", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(data);

            if (arr == null)
            {
                return null;
            }

            var count = arr.GetType().GetProperty("Count");
            var n = count != null ? (int)count.GetValue(arr)! : -1;
            if (playerId >= n)
            {
                return null;
            }

            return arr.GetType().GetProperty("Item")?.GetValue(arr, new object[] { (int)playerId });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>旧版数据层: 标记为存活, 并清出死亡名单。</summary>
    internal static void Revive(byte playerId)
    {
        try
        {
            var pd = PlayerDataOf(playerId);
            if (pd != null)
            {
                var m = pd.GetType().GetMethod("Revive",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (m != null)
                {
                    var ps = m.GetParameters();
                    m.Invoke(pd, ps.Length == 0 ? null : new object[] { true });
                }
            }
        }
        catch { }

        try
        {
            var data = Data();
            var dead = data?.GetType()
                .GetField("deadPlayers", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(data);

            dead?.GetType().GetMethod("Remove", new[] { typeof(byte) })
                ?.Invoke(dead, new object[] { playerId });
        }
        catch { }
    }

    /// <summary>旧版数据层: 标记为死亡, 并登记进死亡名单。已死则不重复写。</summary>
    internal static void Die(byte playerId)
    {
        try
        {
            var pd = PlayerDataOf(playerId);
            if (pd == null)
            {
                return;
            }

            var aliveProp = pd.GetType().GetProperty("IsAlive",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            if (aliveProp?.GetValue(pd) is bool alive && !alive)
            {
                return;   // 已经是死的, 不重复写死亡名单
            }

            // 优先用无参 Die(): 内部就是 Die(PlayerStatus.Dead, 0xFF)
            var die0 = pd.GetType().GetMethod("Die",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, System.Type.EmptyTypes, null);

            if (die0 != null)
            {
                die0.Invoke(pd, null);
                return;
            }

            // 回退: Die(PlayerStatus, byte)
            var deadStatus = FindPlayerStatus("Dead");
            if (deadStatus == null)
            {
                return;
            }

            var die2 = pd.GetType().GetMethod("Die",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { deadStatus.GetType(), typeof(byte) },
                null);

            die2?.Invoke(pd, new[] { deadStatus, (object)byte.MaxValue });
        }
        catch { }
    }

    private static object? FindPlayerStatus(string fieldName)
    {
        try
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("Nebula.Game.PlayerStatus", false);
                if (t == null)
                {
                    continue;
                }

                var v = t.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);

                if (v != null)
                {
                    return v;
                }
            }
        }
        catch { }

        return null;
    }
}
