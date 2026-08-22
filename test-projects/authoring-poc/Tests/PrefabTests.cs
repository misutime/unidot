// SPDX-License-Identifier: MIT
// PrefabTests.cs —— 门禁 5：Prefab override 可查询可解释

using System;
using System.Linq;
using Baize.Authoring;
using Shooter.Gameplay;

namespace AuthoringPoc.Tests;

internal static class PrefabTests
{
	public static void RunPrefabOverrideExplainable(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();
		var world = new AuthoringWorld(schema);
		StableId first = world.AllocateIds(3);
		var protoId = new StableId(first.Value);
		var e1 = new StableId(first.Value + 1);   // 覆盖 Health 的实例
		var e2 = new StableId(first.Value + 2);   // 纯继承的实例

		// 原型：EnemyProto（Health 100、Position 原点）
		var txProto = new AuthoringTransaction();
		txProto.Create(protoId, "EnemyProto");
		txProto.AddComponent(protoId, new Health { Current = 100, Max = 100 }, schema);
		txProto.AddComponent(protoId, new Position { X = 0f, Z = 0f }, schema);
		world.Apply(txProto);

		// 实例化（与 UI/MCP 相同的标准构造：Create + SetPrototype [+ 本地覆盖]）
		var txSpawn = new AuthoringTransaction();
		txSpawn.Create(e1, "Elite1");
		txSpawn.SetPrototype(e1, protoId);
		txSpawn.SetComponent(e1, new Health { Current = 50, Max = 100 }, schema);   // 本地覆盖
		world.Apply(txSpawn);

		var txPlain = new AuthoringTransaction();
		txPlain.Create(e2, "Grunt1");
		txPlain.SetPrototype(e2, protoId);
		world.Apply(txPlain);

		// —— 有效组件解析：本地覆盖生效、继承默认可用 ——
		var effective1 = world.ResolveEffectiveComponents(world.Require(e1));
		check(((Health)effective1[typeof(Health)]).Current == 50, "实例覆盖值应生效");
		check(((Position)effective1[typeof(Position)]).X == 0f, "未覆盖的组件应从原型继承");
		var effective2 = world.ResolveEffectiveComponents(world.Require(e2));
		check(((Health)effective2[typeof(Health)]).Current == 100, "纯继承实例应取原型值");

		// —— 可查询：override 记录在对象上 ——
		check(world.Require(e1).OverriddenComponents.Contains("Shooter.Gameplay.Health"),
			"被覆盖的组件类型应出现在 OverriddenComponents");
		check(world.Require(e2).OverriddenComponents.Count == 0, "纯继承实例应无 override 记录");

		// —— 可解释：每个组件都能回答"值从哪来" ——
		var explanations1 = world.ExplainComponents(e1).ToDictionary(x => x.ComponentTypeName);
		check(explanations1["Shooter.Gameplay.Health"].Source == "local",
			"Health 应解释为 local");
		check(explanations1["Shooter.Gameplay.Health"].Detail.Contains("50")
			&& explanations1["Shooter.Gameplay.Health"].Detail.Contains("100"),
			$"解释应同时给出本地值与原型值：{explanations1["Shooter.Gameplay.Health"].Detail}");
		check(explanations1["Shooter.Gameplay.Position"].Source == "inherited"
			&& explanations1["Shooter.Gameplay.Position"].Detail.Contains(protoId.ToString()),
			"Position 应解释为 inherited 并指明来源原型");

		var explanations2 = world.ExplainComponents(e2);
		check(explanations2.All(x => x.Source == "inherited"), "无覆盖实例的全部组件应解释为 inherited");

		// —— 显式删除也是 override：Remove 后有效集里没有，但解释为 removed-override ——
		var removeTx = new AuthoringTransaction();
		removeTx.RemoveComponent<Position>(e1, schema);
		world.Apply(removeTx);
		var effectiveAfterRemove = world.ResolveEffectiveComponents(world.Require(e1));
		check(!effectiveAfterRemove.ContainsKey(typeof(Position)), "显式删除后有效组件不应含 Position");
		var afterRemove = world.ExplainComponents(e1).ToDictionary(x => x.ComponentTypeName);
		check(afterRemove["Shooter.Gameplay.Position"].Source == "removed-override",
			"显式删除应解释为 removed-override");

		// —— Undo 恢复覆盖状态 ——
		world.Undo();   // 撤销 RemoveComponent
		check(((Position)world.ResolveEffectiveComponents(world.Require(e1))[typeof(Position)]).X == 0f,
			"Undo 显式删除后应恢复继承");

		Console.WriteLine("authoring-poc: Prefab override 可查询可解释验证通过");
	}
}
