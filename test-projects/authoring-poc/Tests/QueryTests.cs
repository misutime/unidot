// SPDX-License-Identifier: MIT
// QueryTests.cs —— 结构化查询（人/AI 共用）

using System;
using System.Linq;
using Baize.Authoring;
using Shooter.Gameplay;

namespace AuthoringPoc.Tests;

internal static class QueryTests
{
	public static void RunStructuredQuery(Action<bool, string> check)
	{
		var (world, ids) = TestSupport.BuildScene();
		// 场景血量：Enemy1=30、Enemy2=60；Player 无 Health。

		// —— 数据形态查询（MCP 从 JSON 构造同一结构）："Health<50 的敌人" ——
		var query = new AuthoringQuery()
			.Require("Shooter.Gameplay.EnemyFaction")     // 标签组件参与过滤
			.Require("Shooter.Gameplay.Health")
			.Where("Shooter.Gameplay.Health", "Current", QueryOperator.LessThan, 50);
		var hits = world.Execute(query);

		check(hits.Count == 1, $"Health<50 的敌人应只有 1 个，实际 {hits.Count}");
		check(hits.Single().Id == ids.Enemy1, $"命中的应是 Enemy1，实际 {hits.Single().Name}");

		// 玩家没有 EnemyFaction/Health——即使数值条件满足也不该出现
		var anyLowValue = new AuthoringQuery()
			.Where("Shooter.Gameplay.Position", "X", QueryOperator.Equal, 0f);
		check(world.Execute(anyLowValue).Select(o => o.Id).Contains(ids.Enemy1),
			"无 Require 时按字段条件直接过滤");
		// 名字过滤 + 组合条件（>= 与名字包含）
		var named = new AuthoringQuery()
			.Named("Enemy")
			.Where("Shooter.Gameplay.Health", "Current", QueryOperator.GreaterOrEqual, 60);
		check(world.Execute(named).Single().Id == ids.Enemy2, "名字+组合条件应命中 Enemy2");

		// 字符串字段比较路径 + NotEqual
		var renamed = new AuthoringQuery().Where("Shooter.Gameplay.PreviousPosition", "X", QueryOperator.NotEqual, 0f);
		check(world.Execute(renamed).Count == 1, "NotEqual 数值路径应命中 Enemy2(X=5)");
		// —— JsonElement 条件值（MCP 反序列化路径）：object 字段实际是 JsonElement ——
		var jsonQuery = new AuthoringQuery()
			.Require("Shooter.Gameplay.EnemyFaction")
			.Where("Shooter.Gameplay.Health", "Current", QueryOperator.LessThan, TestSupport.Json("50"));
		var jsonHits = world.Execute(jsonQuery);
		check(jsonHits.Count == 1 && jsonHits[0].Id == ids.Enemy1, "JsonElement 条件值应与强类型等价");

		// —— 强类型谓词查询（C# 编辑器代码路径）——
		var predicateHits = world.Execute((Health h) => h.Current < 50);
		check(predicateHits.Count == 1 && predicateHits[0].Object.Id == ids.Enemy1,
			"强类型谓词查询应与数据形态查询一致");

		// 查询结果确定性：多次执行顺序稳定（StableId 升序）
		var first = world.Execute(new AuthoringQuery().Named("Enemy"));
		var second = world.Execute(new AuthoringQuery().Named("Enemy"));
		check(first.Select(o => o.Id).SequenceEqual(second.Select(o => o.Id)), "查询结果顺序应确定");
		check(first.First().Id.CompareTo(first.Last().Id) < 0, "结果应按 StableId 升序");
		Console.WriteLine("authoring-poc: 结构化查询验证通过（数据形态 + 强类型谓词两条路径）");
	}
}
