// SPDX-License-Identifier: MIT
// MovementComponents.cs —— 移动事实：运行状态与每实体参数分开

using Baize.Authoring;
using Friflo.Engine.ECS;

namespace Shooter.Gameplay;

// 运行状态：每 Tick 都可能变化。
[Component]
public struct Position : IComponent { public float X, Z; }
[Component]
public struct PreviousPosition : IComponent { public float X, Z; }
[Component]
public struct Velocity : IComponent { public float X, Z; }

// 每实体参数：同类实体可以有不同移动速度，但它不是计时器或瞬时状态。
[Component]
public struct MoveSpeed : IComponent { public float Value; }
