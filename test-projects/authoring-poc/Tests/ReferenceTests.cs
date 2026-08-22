// SPDX-License-Identifier: MIT
// ReferenceTests.cs —— 门禁 3：Rename/Reparent 不破坏引用

using System;
using System.Linq;
using Baize.Authoring;
using Shooter.Gameplay;
namespace AuthoringPoc.Tests;

internal static class ReferenceTests
{
	public static void RunRenameReparentKeepReferences(Action<bool, string> check)
	{
		var (world, ids) = TestSupport.BuildScene();

		// 建立引用：Player —[Targets]→ Enemy1（关系引用按 StableId）
		var linkTx = new AuthoringTransaction();
		linkTx.AddRelation(ids.Player, "Targets", ids.Enemy1);
		world.Apply(linkTx);
		int relationsBefore = world.Require(ids.Player).Relations.Count;

		// Rename + Reparent：Enemy1 改名并移出 EnemyGroup
		var tx = new AuthoringTransaction();
		tx.Rename(ids.Enemy1, "RenamedEnemy");
		tx.Reparent(ids.Enemy1, StableId.None);   // 移到根
		world.Apply(tx);

		check(world.Exists(ids.Enemy1), "Rename/Reparent 后对象身份不变（同一 StableId 仍存在）");
		check(world.Find(ids.Enemy1)!.Name == "RenamedEnemy", "名字应已更新");

		// 引用完好：Player 的关系目标仍是同一个 Id
		var player = world.Require(ids.Player);
		check(player.Relations.Count == relationsBefore, "引用数量不应变化");
		check(player.Relations.Single(r => r.RelationType == "Targets").TargetId == ids.Enemy1,
			"关系引用应指向原 StableId（不被改名破坏）");

		// 子对象 C 的 ParentId 不受父级改名影响；Enemy2 仍挂在 Group 下
		check(world.ChildrenOf(ids.Group).Contains(ids.Enemy2), "未移动的兄弟对象不受影响");
		check(!world.ChildrenOf(ids.Group).Contains(ids.Enemy1), "被移走的孩子不再属于旧父级");
		check(world.Require(ids.Enemy2).ParentId == ids.Group, "Enemy2 父级保持不变");

		// 按名字找旧名 → 找不到了；但逻辑引用（查询）全部照常命中
		check(world.FindByName("Enemy1") is null, "旧名字不再可寻（名字不是身份）");
		var query = new AuthoringQuery().Require<Health>().Where<Health>("Current", QueryOperator.LessThan, 100);
		var hitIds = world.Execute(query).Select(o => o.Id).ToHashSet();
		check(hitIds.Contains(ids.Enemy1), "结构化查询仍能通过稳定 Id 命中改名后的对象");

		// Undo 后名字与父级一起恢复，引用依旧
		world.Undo();
		check(world.Find(ids.Enemy1)!.Name == "Enemy1", "Undo 应恢复旧名");
		check(world.Require(ids.Enemy1).ParentId == ids.Group, "Undo 应恢复旧父级");
		check(player.Relations.Single(r => r.RelationType == "Targets").TargetId == ids.Enemy1,
			"全程关系引用从未断裂");

		Console.WriteLine("authoring-poc: Rename/Reparent 不破坏引用验证通过");
	}
}
