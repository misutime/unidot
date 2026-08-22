// SPDX-License-Identifier: MIT
// BakerTests.cs —— 门禁 4：单组件修改只重烘相关对象（增量烘焙）

using System;
using System.Linq;
using Baize.Authoring;
using Shooter.Gameplay;

namespace AuthoringPoc.Tests;

internal static class BakerTests
{
	public static void RunIncrementalBakeOnlyRebakesDirty(Action<bool, string> check)
	{
		var (world, ids) = TestSupport.BuildScene();
		var baker = new SceneBaker(world.Schema);

		// 全量烘焙
		BakedScene scene = baker.Bake(world);
		check(baker.LastBakedObjectCount == world.ObjectCount,
			$"全量烘焙应重烘全部 {world.ObjectCount} 个对象，实际 {baker.LastBakedObjectCount}");
		check(scene.Count == world.ObjectCount, "场景对象数应一致");

		BakedObject bakedPlayer = scene.Find(ids.Player)!;
		BakedObject bakedEnemy1 = scene.Find(ids.Enemy1)!;

		// 只改 Enemy1 的 Health → 只有它重烘
		var tx = new AuthoringTransaction();
		tx.SetComponent(ids.Enemy1, new Health { Current = 5, Max = 30 }, world.Schema);
		world.Apply(tx);

		baker.Bake(world, scene);
		check(baker.LastBakedObjectCount == 1,
			$"单组件修改后增量烘焙应只重烘 1 个对象，实际 {baker.LastBakedObjectCount}");
		check(ReferenceEquals(scene.Find(ids.Player), bakedPlayer), "未变对象的 BakedObject 实例必须原样保留");
		check(!ReferenceEquals(scene.Find(ids.Enemy1), bakedEnemy1), "被修改的对象应是新实例");
		check(((Health)scene.Find(ids.Enemy1)!.Components[typeof(Health)]).Current == 5,
			"重烘后的对象应携带新值");

		// 无变化时零重烘
		baker.Bake(world, scene);
		check(baker.LastBakedObjectCount == 0, $"无变化时应零重烘，实际 {baker.LastBakedObjectCount}");


		// 烘焙对象带关系与层级父
		var linkTx = new AuthoringTransaction();
		linkTx.AddRelation(ids.Player, "Targets", ids.Enemy1);
		linkTx.Reparent(ids.Player, ids.Group);   // 玩家挂到 EnemyGroup 下
		world.Apply(linkTx);
		baker.Bake(world, scene);
		check(baker.LastBakedObjectCount == 1, "关系/层级修改应只脏化玩家一个对象");
		check(scene.Find(ids.Player)!.Relations.Single().TargetId == ids.Enemy1,
			"关系应随对象进入烘焙产物");
		check(scene.Find(ids.Player)!.ParentId == ids.Group, "层级父应进入烘焙产物");

		// 删除对象 → 增量同步删除
		var deleteTx = new AuthoringTransaction();
		deleteTx.Delete(ids.Group);   // 级联删 Group/Enemy1/Enemy2/（玩家也在其下）
		world.Apply(deleteTx);
		baker.Bake(world, scene);
		check(scene.Count == 0, $"级联删除含玩家后场景应为空，实际 {scene.Count}");
		check(baker.LastBakedObjectCount == 4, "4 个被删对象都应被移除");
		Console.WriteLine("authoring-poc: 增量烘焙验证通过（单组件修改只重烘相关对象）");
	}
}
