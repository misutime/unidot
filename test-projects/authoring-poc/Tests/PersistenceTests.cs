// SPDX-License-Identifier: MIT
// PersistenceTests.cs —— 确定性持久化：Save → Load → Save 逐字节相同，hash 不变

using System;
using System.IO;
using System.Linq;
using Baize.Authoring;

namespace AuthoringPoc.Tests;

internal static class PersistenceTests
{
	public static void RunRoundTripIsByteStable(Action<bool, string> check)
	{
		var (world, ids) = TestSupport.BuildScene();

		// 加"会破坏确定性/加载顺序"的数据：前向引用（小 Id 引用大 Id）+ 乱序关系
		var tx = new AuthoringTransaction();
		tx.Reparent(ids.Player, ids.Group);          // o1.parent = o2（文件中 o1 先于 o2 出现）
		tx.SetPrototype(ids.Enemy1, ids.Enemy2);     // o3.prototype = o4（前向原型）
		tx.AddRelation(ids.Enemy2, "Targets", ids.Player);
		tx.AddRelation(ids.Enemy1, "Targets", ids.Player);
		world.Apply(tx);
		string dir = ".tmp";
		Directory.CreateDirectory(dir);
		string pathA = Path.Combine(dir, "authoring-roundtrip-a.bscene");
		string pathB = Path.Combine(dir, "authoring-roundtrip-b.bscene");

		try
		{
			ulong hashBefore = world.ComputeArtifactHash();

			AuthoringSceneFile.Save(world, pathA);
			byte[] bytesA = File.ReadAllBytes(pathA);

			// 往返：Load 成新世界再保存
			AuthoringWorld loaded = AuthoringSceneFile.Load(pathA, world.Schema);
			if (loaded.ComputeArtifactHash() != hashBefore)
			{
				Console.WriteLine($"  [diag] hashBefore={hashBefore}, loaded={loaded.ComputeArtifactHash()}");
				foreach (var id in new[] { ids.Player, ids.Group, ids.Enemy1, ids.Enemy2 })
				{
					var a = world.Require(id);
					var b = loaded.Find(id);
					if (b is null) { Console.WriteLine($"  [diag] {id}: 缺失"); continue; }
					Console.WriteLine($"  [diag] {id} '{a.Name}'/'{b.Name}' parent {a.ParentId}/{b.ParentId} proto {a.PrototypeId?.ToString() ?? "-"}/{b.PrototypeId?.ToString() ?? "-"} " +
						$"comp {a.Components.Count}/{b.Components.Count} rel {a.Relations.Count}/{b.Relations.Count} ov {a.OverriddenComponents.Count}/{b.OverriddenComponents.Count}");
					foreach (var type in a.Components.Keys)
					{
						if (!b.Components.TryGetValue(type, out var bv) || !world.Schema.Get(type).ValueEquals(a.Components[type], bv))
							Console.WriteLine($"  [diag]   组件差异: {type}");
					}
				}
			}
			check(loaded.ComputeArtifactHash() == hashBefore, "Load 后 Artifact hash 应与保存前一致");
			check(loaded.ObjectCount == world.ObjectCount, "Load 后对象数应一致");

			// 层级/关系/组件值逐一核对（含前向引用：Player.parent=Group、Enemy1.prototype=Enemy2）
			check(loaded.Require(ids.Player).ParentId == ids.Group, "往返后前向层级引用保持");
			check(loaded.Require(ids.Enemy1).PrototypeId == ids.Enemy2, "往返后前向原型引用保持");
			check(loaded.Require(ids.Enemy2).Relations.Single().TargetId == ids.Player,
				"往返后关系目标保持");
			check(loaded.ChildrenOf(ids.Group).Count == 3, "往返后 children 索引重建正确（含移入的 Player）");

			AuthoringSceneFile.Save(loaded, pathB);
			byte[] bytesB = File.ReadAllBytes(pathB);
			check(bytesA.SequenceEqual(bytesB), "Save→Load→Save 必须逐字节相同（Git 友好）");

			// nextId 恢复：加载后新分配的 Id 不与已有对象冲突
			StableId allocated = loaded.AllocateId();
			check(!loaded.Exists(allocated) && allocated.Value > ids.Enemy2.Value,
				"加载后分配的 Id 应大于场景内全部 Id");

			// 往返后的世界继续可用：事务 + undo 正常
			var postTx = new AuthoringTransaction();
			postTx.Rename(ids.Player, "RenamedAfterLoad");
			loaded.Apply(postTx);
			check(loaded.FindByName("RenamedAfterLoad") is not null, "往返后的世界可继续编辑");
			loaded.Undo();
			check(loaded.FindByName("Player") is not null, "往返后 undo 可用");
		}
		finally
		{
			File.Delete(pathA);
			File.Delete(pathB);
		}
		Console.WriteLine("authoring-poc: 确定性持久化验证通过（字节稳定 + hash 保持 + 继续可编辑）");
	}

	/// <summary>P2.4 review 第2轮验证：空场景、装载基线、预留 Id、清原型 override 的往返语义。</summary>
	public static void RunLoadBaselineAndEdgeCases(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();
		string dir = ".tmp";
		Directory.CreateDirectory(dir);
		string path = Path.Combine(dir, "authoring-edge.bscene");

		try
		{
			// —— 空场景往返 ——
			var empty = new AuthoringWorld(schema);
			AuthoringSceneFile.Save(empty, path);
			var loadedEmpty = AuthoringSceneFile.Load(path, schema);
			check(loadedEmpty.ObjectCount == 0, "空场景应能往返（objects=[]）");
			check(loadedEmpty.ComputeArtifactHash() == empty.ComputeArtifactHash(), "空场景往返 hash 一致");

			// —— 装载后是干净基线：无历史、nextId 保持文件值 ——
			check(!loadedEmpty.CanUndo && !loadedEmpty.CanRedo, "装载后的世界不应有历史");

			// —— 清除原型后的 override 往返：SetPrototype(None) 事务化清空，Save 不再携带 ——
			var world = new AuthoringWorld(schema);
			StableId first = world.AllocateIds(2);
			var protoId = new StableId(first.Value);
			var instanceId = new StableId(first.Value + 1);
			var setup = new AuthoringTransaction();
			setup.Create(protoId, "Proto");
			setup.AddComponent(protoId, new Shooter.Gameplay.Health { Current = 10, Max = 10 }, schema);
			setup.Create(instanceId, "Instance");
			setup.SetPrototype(instanceId, protoId);
			setup.SetComponent(instanceId, new Shooter.Gameplay.Health { Current = 5, Max = 10 }, schema);
			world.Apply(setup);
			check(world.Require(instanceId).OverriddenComponents.Count == 1, "覆盖应已记录");

			var clearTx = new AuthoringTransaction();
			clearTx.SetPrototype(instanceId, StableId.None);
			world.Apply(clearTx);
			check(world.Require(instanceId).OverriddenComponents.Count == 0,
				"清除原型应事务化清空 override 记录");
			ulong hashAfterClear = world.ComputeArtifactHash();

			AuthoringSceneFile.Save(world, path);
			var loaded = AuthoringSceneFile.Load(path, schema);
			check(loaded.ComputeArtifactHash() == hashAfterClear,
				"清除原型后的世界往返 hash 应一致（override 不复活）");

			// —— Undo 不回收事务外预留的 Id ——
			var renameTx = new AuthoringTransaction();
			renameTx.Rename(protoId, "ProtoRenamed");
			world.Apply(renameTx);
			StableId reserved = world.AllocateId();   // 事务外预留
			world.Undo();   // 撤销改名——预留必须保留
			check(world.AllocateId().Value > reserved.Value,
				"Undo 不得回收事务外预留的 Id（避免重复发出）");
			check(world.FindByName("Proto") is not null, "改名撤销本身应生效");
		}
		finally
		{
			File.Delete(path);
		}

		Console.WriteLine("authoring-poc: 装载基线/空场景/预留 Id/清原型 override 验证通过");
	}

	/// <summary>P2.4 review 第2轮验证：完整查询 JSON（MCP 路径）反序列化后条件完整。</summary>
	public static void RunQueryJsonDeserialization(Action<bool, string> check)
	{
		const string json = """
			{
			  "RequiredComponents": ["Shooter.Gameplay.EnemyFaction", "Shooter.Gameplay.Health"],
			  "Conditions": [
			    { "ComponentType": "Shooter.Gameplay.Health", "FieldName": "Current", "Operator": "LessThan", "Value": 50 }
			  ],
			  "NameContains": "Enemy"
			}
			""";
		var query = System.Text.Json.JsonSerializer.Deserialize<AuthoringQuery>(json);
		check(query is not null, "查询 JSON 应可反序列化");
		check(query!.RequiredComponents.Count == 2, $"反序列化应恢复 2 个 Require，实际 {query.RequiredComponents.Count}");
		check(query.Conditions.Count == 1, $"反序列化应恢复 1 个条件，实际 {query.Conditions.Count}");

		var (world, ids) = TestSupport.BuildScene();
		var hits = world.Execute(query);
		check(hits.Count == 1 && hits[0].Id == ids.Enemy1,
			$"反序列化查询应命中 Enemy1，实际 {hits.Count} 个");
	}
}
