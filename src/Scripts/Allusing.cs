// ============================================================
// Allusing.cs — 本 addon 的全局 using
//
// ⚠️ NOS 编译 addon 时【不会】自动注入 Virial.* / Nebula.* 命名空间。
//    各源码文件里直接使用 NebulaAPI / GamePlayer / RoleAssignType 等类型,
//    全部依赖这里的 global using。
//    没有这个文件会出现成片的 CS0246 / CS0103。
//
//    本清单取自 NOS 自带 addon 的实际可用集, 并按本插件的编译结果修正:
//      · AmongUsClient 位于【全局命名空间】(不在 InnerNet 下)
//      · 不用 `global using GamePlayer = Virial.Game.Player;` 别名 ——
//        该别名在当前编译器下不生效, 因此直接用 Virial.Game.Player
// ============================================================

global using System;
global using System.Collections.Generic;
global using System.Linq;
global using Virial;
global using Virial.Assignable;
global using Virial.Attributes;
global using Virial.Components;
global using Virial.Configuration;
global using Virial.Events;
global using Virial.Events.Game;
global using Virial.Events.Player;
global using Virial.Game;
global using Virial.Media;
global using Virial.Text;
global using Virial.Compat;
global using Virial.Runtime;
global using UnityEngine;
global using Nebula;
global using Nebula.Modules;
global using InnerNet;   // InnerNetClient / GameStates 在这里
global using Nebula.Modules.ScriptComponents;
