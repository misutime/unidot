// SPDX-License-Identifier: MIT
// TransactionTests.cs —— 门禁 1/2 + 原子性：事务、diff、Undo/Redo

using System;
using System.Collections.Generic;
using System.Linq;
using Baize.Authoring;
using Shooter.Gameplay;

namespace AuthoringPoc.Tests;

internal static class TransactionTests
{
	/// <summary>门禁 1：同一操作经 UI（强类型）和 MCP（JSON op）产生相同事务与 diff。</summary>
	public static void RunUiAndMcpProduceSameTransactionAndDiff(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();

		// —— UI 路径：编辑器面板用强类型便捷构造 ——

		var (uiWorld, uiIds) = TestSupport.BuildScene();
		var uiTx = new AuthoringTransaction();
		uiTx.Rename(uiIds.Enemy1, "EliteEnemy");
		uiTx.SetComponent(uiIds.Enemy1, new Health { Current = 50, Max = 100 }, schema);
		AuthoringDiff uiDiff = uiWorld.Apply(uiTx);

		// —— MCP 路径：工具层从 JSON 直接构造原始 op（组件值是同一段 JSON 语义） ——

		var (mcpWorld, mcpIds) = TestSupport.BuildScene();
		var mcpTx = new AuthoringTransaction();
		mcpTx.Add(new RenameObjectOp(mcpIds.Enemy1, "EliteEnemy"));
		mcpTx.Add(new SetComponentOp(
			mcpIds.Enemy1,
			"Shooter.Gameplay.Health",
			TestSupport.Json("{\"Current\":50,\"Max\":100}")));
		AuthoringDiff mcpDiff = mcpWorld.Apply(mcpTx);

		check(SameOps(uiTx.Ops, mcpTx.Ops), $"UI 与 MCP 构造的事务不同：{Describe(uiTx)} vs {Describe(mcpTx)}");
		check(Equals(uiDiff, mcpDiff), $"UI 与 MCP 的 diff 不同：{uiDiff} vs {mcpDiff}");
		check(uiWorld.ComputeArtifactHash() == mcpWorld.ComputeArtifactHash(),
			"UI 与 MCP 路径应用后的 Artifact hash 不同");

		Console.WriteLine($"authoring-poc: UI/MCP 同事务同 diff 验证通过（diff={uiDiff.Entries.Count} 条，hash 一致）");
	}

	/// <summary>事务原子性：中途失败时世界保持原状。</summary>
	public static void RunAtomicity(Action<bool, string> check)
	{
		var (world, ids) = TestSupport.BuildScene();
		ulong before = world.ComputeArtifactHash();
		ulong versionBefore = world.Version;

		var tx = new AuthoringTransaction();
		tx.Rename(ids.Player, "RenamedPlayer");   // 第一个 op 合法
		tx.Add(new SetComponentOp(ids.Enemy1, "Shooter.Gameplay.Health",
			TestSupport.Json("{\"Current\":1,\"Max\":1}")));   // 第二个 op 也合法
		tx.Add(new RenameObjectOp(new StableId(999), "Ghost"));   // 第三个 op 失败：对象不存在

		bool threw = false;
		try
		{
			world.Apply(tx);
		}
		catch (AuthoringTransactionException ex)
		{
			threw = true;
			check(ex.Message.Contains("3"), $"异常应指出失败位置（第 3 个操作）：{ex.Message}");
		}
		check(threw, "非法事务应抛 AuthoringTransactionException");
		check(world.ComputeArtifactHash() == before, "事务回滚后 Artifact hash 应与 Apply 前一致");
		check(world.Version == versionBefore, "事务回滚后版本号不应推进");
		check(world.Find(ids.Player)!.Name == "Player", "回滚后第一个 op 的效果也不应保留");
		Console.WriteLine("authoring-poc: 事务原子性验证通过");
	}

	/// <summary>门禁 2：Undo/Redo 后 Artifact hash 完全恢复（含级联删除的恢复）。</summary>
	public static void RunUndoRedoRestoresArtifactHash(Action<bool, string> check)
	{
		var (world, ids) = TestSupport.BuildScene();
		ulong h0 = world.ComputeArtifactHash();

		// 事务 A：改玩家数值 + 改敌人血量
		var txA = new AuthoringTransaction();
		txA.SetComponent(ids.Player, new Position { X = 2f, Z = 3f }, world.Schema);
		txA.SetComponent(ids.Enemy1, new Health { Current = 10, Max = 30 }, world.Schema);
		world.Apply(txA);
		ulong hA = world.ComputeArtifactHash();

		// 事务 B：级联删除 EnemyGroup（连 Enemy1/Enemy2 一起删）
		var txB = new AuthoringTransaction();
		txB.Delete(ids.Group);
		world.Apply(txB);
		ulong hB = world.ComputeArtifactHash();
		check(hB != hA && hA != h0, "事务应改变场景 hash");
		check(!world.Exists(ids.Enemy1), "级联删除后 Enemy1 应不存在");

		// Undo B → 恢复整棵子树
		world.Undo();
		check(world.ComputeArtifactHash() == hA, "Undo 级联删除后 hash 应恢复到事务 A 后状态");
		check(world.Exists(ids.Enemy1) && world.Exists(ids.Enemy2), "Undo 后子树对象应全部恢复");

		// Undo A → 回到初始
		world.Undo();
		check(world.ComputeArtifactHash() == h0, "Undo 全部后 hash 应恢复初始状态");
		var health = world.Require(ids.Enemy1).Components[typeof(Health)];
		check(((Health)health).Current == 30, "Undo 后组件值应为旧值（30）");

		// Redo ×2 → 重放到 hB
		world.Redo();
		check(world.ComputeArtifactHash() == hA, "Redo 事务 A 后 hash 应为 hA");
		world.Redo();
		check(world.ComputeArtifactHash() == hB, "Redo 事务 B 后 hash 应为 hB");
		check(!world.Exists(ids.Group), "Redo 级联删除再次生效");

		// 再 Undo → hA；新事务分支清空 redo
		world.Undo();
		check(world.ComputeArtifactHash() == hA, "再 Undo 后 hash 应回到 hA");
		check(world.CanRedo, "Undo 后应可 Redo");

		var txC = new AuthoringTransaction();
		txC.Rename(ids.Player, "BranchedPlayer");
		world.Apply(txC);
		check(!world.CanRedo, "新事务应清空 redo 栈");

		Console.WriteLine("authoring-poc: Undo/Redo hash 完全恢复验证通过（含级联删除恢复与 redo 分支清理）");
	}

	internal static bool SameOps(IReadOnlyList<AuthoringOp> left, IReadOnlyList<AuthoringOp> right)
	{
		if (left.Count != right.Count) return false;
		for (int index = 0; index < left.Count; index++)
		{
			if (Equals(left[index], right[index])) continue;

			if (left[index] is SetComponentOp lSet && right[index] is SetComponentOp rSet)
			{
				Console.WriteLine($"  [diag] op#{index} Id: {lSet.Id} vs {rSet.Id}; " +
					$"type: '{lSet.ComponentType}' vs '{rSet.ComponentType}'; " +
					$"value: {lSet.Value.GetRawText()} vs {rSet.Value.GetRawText()}");
			}
			else if (left[index] is RenameObjectOp lRename && right[index] is RenameObjectOp rRename)
			{
				Console.WriteLine($"  [diag] op#{index} rename: ({lRename.Id},'{lRename.NewName}') vs ({rRename.Id},'{rRename.NewName}')");
			}
			return false;
		}
		return true;
	}

	private static string Describe(AuthoringTransaction transaction) =>
		string.Join(", ", transaction.Ops.Select(op => op.GetType().Name));

	/// <summary>P1 修复验证：自动分配 Id 的计数器必须纳入回滚/Undo（hash 完全恢复）。</summary>
	public static void RunAutoIdCounterIsTransactional(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();
		var world = new AuthoringWorld(schema);
		ulong h0 = world.ComputeArtifactHash();

		var tx = new AuthoringTransaction();
		tx.Create(StableId.None, "AutoObject");   // 自动分配 Id
		world.Apply(tx);
		ulong hAfter = world.ComputeArtifactHash();
		check(hAfter != h0, "自动创建应改变 hash");

		world.Undo();
		check(world.ComputeArtifactHash() == h0, "撤销自动创建后 hash 必须完全恢复（含计数器回退）");
		world.Redo();
		check(world.ComputeArtifactHash() == hAfter, "Redo 后 hash 应回到事务后状态");

		// 失败事务：自动分配已推进，回滚必须一并还原
		var badTx = new AuthoringTransaction();
		badTx.Create(StableId.None, "WillRollback");
		badTx.Add(new RenameObjectOp(new StableId(999), "Ghost"));
		bool threw = false;
		try { world.Apply(badTx); }
		catch (AuthoringTransactionException) { threw = true; }
		check(threw, "失败事务应抛出");
		check(world.ComputeArtifactHash() == hAfter, "失败事务回滚后 hash 应与 Apply 前一致");

		// 显式大 Id 推进计数器：后续自动分配不得撞上已占用 Id
		var bigTx = new AuthoringTransaction();
		bigTx.Create(new StableId(50), "ExplicitBig");
		world.Apply(bigTx);
		var autoTx = new AuthoringTransaction();
		autoTx.Create(StableId.None, "AfterBig");
		var diff = world.Apply(autoTx);
		check(diff.Entries[0].Detail.Contains("o51"),
			$"显式大 Id 之后自动分配应从 o51 开始，实际 diff：{diff.Entries[0].Detail}");

		Console.WriteLine("authoring-poc: 自动 Id 计数器事务化验证通过（undo/回滚/显式大 Id）");
	}

	/// <summary>P1 修复验证：语义等价但词法不同的 JSON 经 Canonicalize 收敛为同一事务。</summary>
	public static void RunCanonicalizeMergesEquivalentJson(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();

		// MCP 路径：字段倒序 + 多余空白（语义与 UI 强类型路径相同）
		var mcpTx = new AuthoringTransaction();
		mcpTx.Add(new SetComponentOp(
			new StableId(7),
			"Shooter.Gameplay.Health",
			TestSupport.Json("{ \"Max\":100 ,\"Current\":50 }")));
		var canonical = mcpTx.Canonicalize(schema!);
		check(canonical.Ops.Count == 1, "规范化副本应有 1 个 op");
		var canonicalOp = (SetComponentOp)canonical.Ops[0];
		check(canonicalOp.Value.GetRawText() == "{\"Current\":50,\"Max\":100}",
			$"规范化后的组件值应为 Schema 键序：{canonicalOp.Value.GetRawText()}");
		var rawOriginal = (SetComponentOp)mcpTx.Ops[0];
		check(canonicalOp.Value.GetRawText() != rawOriginal.Value.GetRawText(),
			"原始输入与规范化输出不同（证明规范化生效）");

		// 门禁语义：UI 强类型路径与 MCP 倒序 JSON 路径，规范化后 op 完全一致
		var uiTx = new AuthoringTransaction();
		uiTx.SetComponent(new StableId(7), new Health { Current = 50, Max = 100 }, schema);
		check(SameOps(uiTx.Canonicalize(schema).Ops, canonical.Ops),
			$"UI 与 MCP 规范化后的事务应完全相同");
	}

	/// <summary>P1 修复验证：删除仍被引用的对象必须被拒绝且世界不变。</summary>
	public static void RunDeleteRejectsExternalReferences(Action<bool, string> check)
	{
		var (world, ids) = TestSupport.BuildScene();
		ulong before = world.ComputeArtifactHash();

		// Player 的原型指向 EnemyGroup → 删除 Group 应被拒绝
		var linkTx = new AuthoringTransaction();
		linkTx.SetPrototype(ids.Player, ids.Group);
		world.Apply(linkTx);

		var deleteTx = new AuthoringTransaction();
		deleteTx.Delete(ids.Group);
		bool threw = false;
		try { world.Apply(deleteTx); }
		catch (AuthoringTransactionException ex)
		{
			threw = true;
			check(ex.Message.Contains("原型"), $"拒绝原因应指出原型引用：{ex.Message}");
		}
		check(threw, "删除被实例引用的原型应被拒绝");
		check(world.Exists(ids.Group), "被拒删除不应生效");
		check(world.ComputeArtifactHash() != before, "链接操作本身已生效（hash 变化属预期）");

		// 解除引用后删除成功
		var unlinkTx = new AuthoringTransaction();
		unlinkTx.Add(new SetPrototypeOp(ids.Player, StableId.None));
		world.Apply(unlinkTx);
		world.Apply(deleteTx);
		check(!world.Exists(ids.Group), "解除引用后删除应成功");

		// 关系入引用同样拦截
		var relWorld = TestSupport.BuildScene().World;
		var watcherId = relWorld.AllocateId();
		var relLink = new AuthoringTransaction();
		relLink.Create(watcherId, "Watcher");
		relLink.AddRelation(watcherId, "Watch", ids.Enemy1);   // 新对象关系指向 Enemy1
		relWorld.Apply(relLink);
		var relDelete = new AuthoringTransaction();
		relDelete.Delete(ids.Enemy1);
		bool relThrew = false;
		try { relWorld.Apply(relDelete); }
		catch (AuthoringTransactionException ex)
		{
			relThrew = true;
			check(ex.Message.Contains("关系"), $"拒绝原因应指出关系引用：{ex.Message}");
		}
		check(relThrew, "删除被关系引用的对象应被拒绝");

		Console.WriteLine("authoring-poc: 删除入引用保护验证通过（原型 + 关系）");
	}

	/// <summary>P1 修复验证：沿原型链检测环（而非父子层级）。</summary>
	public static void RunPrototypeCycleRejected(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();
		var world = new AuthoringWorld(schema);
		StableId first = world.AllocateIds(2);
		var a = new StableId(first.Value);
		var b = new StableId(first.Value + 1);

		var setup = new AuthoringTransaction();
		setup.Create(a, "A");
		setup.Create(b, "B");
		setup.SetPrototype(b, a);   // B 的原型是 A
		world.Apply(setup);

		// A 的原型设为 B → 沿 B 的原型链（B→A）会回到 A，必须拒绝
		var cycleTx = new AuthoringTransaction();
		cycleTx.SetPrototype(a, b);
		bool threw = false;
		try { world.Apply(cycleTx); }
		catch (AuthoringTransactionException ex)
		{
			threw = true;
			check(ex.InnerException?.Message.Contains("环") == true || ex.Message.Contains("环"),
				$"应提示形成环：{ex.Message}");
		}
		check(threw, "原型环 A→B→A 必须被拒绝");
		check(world.Require(a).PrototypeId is null, "被拒设置不应生效");

		Console.WriteLine("authoring-poc: 原型链环检测验证通过");
	}
}
