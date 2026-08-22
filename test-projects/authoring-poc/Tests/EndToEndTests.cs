// SPDX-License-Identifier: MIT
// EndToEndTests.cs —— 门禁 6（纯 .NET 版）：W1 数据经 Baker 驱动 W2 真实玩法
//
// W1 Authoring 场景（对象/组件/关系）→ SceneBaker → IRuntimeSceneSource
//   → RuntimeSceneSpawner.Spawn 进 EcsWorld → ShooterFeature 玩法系统照常运行。
// 全程不写一行 SpawnNow 手工装配——数据真正来自 W1。

using System;
using System.Collections.Generic;
using System.Linq;
using Baize.Authoring;
using Baize.Ecs;
using Friflo.Engine.ECS;
using Shooter.Gameplay;
using Position = Shooter.Gameplay.Position;

namespace AuthoringPoc.Tests;

internal static class EndToEndTests
{
	public static void RunW1SceneBakedIntoW2PlaysShooter(Action<bool, string> check)
	{
		// —— 1. W1：作者在编辑器里摆的场景 ——
		var (authoring, ids) = TestSupport.BuildScene();
		var tuneTx = new AuthoringTransaction();
		// 作者在编辑器里把 Enemy1 调成"一击即死"——W1 数据决定 W2 行为
		tuneTx.SetComponent(ids.Enemy1, new Health { Current = 1, Max = 1 }, authoring.Schema);
		tuneTx.AddRelation(ids.Enemy1, "Hunts", ids.Player);   // 关系随场景走（语义层）
		authoring.Apply(tuneTx);

		// —— 2. Baker：W1 → 运行时场景源 ——
		var baker = new SceneBaker(authoring.Schema);
		BakedScene scene = baker.Bake(authoring);
		check(scene.Find(ids.Player)!.Components.ContainsKey(typeof(PlayerFaction)),
			"标签组件应进入烘焙产物");
		var bakedHealth = (Health)scene.Find(ids.Enemy1)!.Components[typeof(Health)];
		check(bakedHealth.Current == 1, "烘焙组件值应反映 W1 里的事务修改（30 → 1）");

		// —— 3. W2：装载场景 + 安装玩法规则（无任何手工 SpawnNow）——
		var world = new EcsWorld(aot => EcsAotRegistration.RegisterAll(aot));
		world
			.InsertState(new SpawnConfig())
			.InsertState(new SpawnState())
			.InsertState(new FireInputState())
			.InsertState(new MatchState())
			.InsertState(new ShooterSnapshotState());
		SpawnConfig config = world.GetState<SpawnConfig>();
		config.Interval = 9999f;   // 关闭自动生成，只测场景内敌人
		config.MaxAlive = 0;       // SpawnState.Remaining 初始为 0，第一 Tick 会触发一次生成——用 MaxAlive=0 硬拦

		Dictionary<StableId, Entity> map = world.Spawn(scene);   // RuntimeSceneSpawner 扩展

		world.GetState<MatchState>().AliveEnemies = 2;   // 场景带 2 个活敌
		world.AddFeature(new ShooterFeature());

		// —— 4. W1 数据确实驱动了 W2 实体 ——
		Entity player = map[ids.Player];
		check(player.Tags.Has<PlayerFaction>(), "玩家实体应带 PlayerFaction 标签");
		check(player.GetComponent<MoveSpeed>().Value == 8f, "MoveSpeed 来自 W1 数据");
		check(player.GetComponent<Position>().Z == 0f, "出生位置来自 W1 数据");
		check(map[ids.Enemy1].GetComponent<SeekTarget>() is { } _, "敌人能力来自 W1 数据");
		check(map[ids.Enemy1].Tags.Has<EnemyFaction>(), "敌人应带 EnemyFaction 标签（命中检测依赖）");
		check(map[ids.Enemy1].HasComponent<Health>(), "敌人应有 Health");

		int entitiesBefore = CountEntities(world);

		// —— 5. 开火脚本：玩家原地开火，+Z 弹道命中 (0,10) 的 Enemy1 ——
		for (int tick = 0; tick < 60; tick++)
		{
			bool fire = tick == 3;
			world.Tick(new InputFrame(0f, 0f, fire));
		}

		MatchState match = world.GetState<MatchState>();
		check(match.Score >= 1, $"Enemy1 应被击杀并计分，实际 score={match.Score}");
		check(match.AliveEnemies == 1, $"存活敌人应剩 Enemy2 一个，实际 {match.AliveEnemies}");
		check(map[ids.Enemy1].IsNull, "被击杀敌人的实体应已失效（Friflo Entity.IsNull）");
		check(CountEntities(world) < entitiesBefore + 10, "实体数量应收敛（投射物已清理或超射程回收中）");

		// —— 6. 增量重烘后重挂载：改值 + 删除组件都应同步到 W2 ——
		var oldBaked = scene.Find(ids.Enemy2);
		var buffTx = new AuthoringTransaction();
		buffTx.SetComponent(ids.Enemy2, new Health { Current = 1, Max = 60 }, authoring.Schema);
		buffTx.RemoveComponent<SeekTarget>(ids.Enemy2, authoring.Schema);   // W1 删除能力组件
		authoring.Apply(buffTx);
		baker.Bake(authoring, scene);
		map[ids.Enemy2].ApplyTo(oldBaked, scene.Find(ids.Enemy2)!);
		check(map[ids.Enemy2].GetComponent<Health>().Current == 1,
			"W1 改值经增量烘焙后应反映到 W2 实体");
		check(!map[ids.Enemy2].HasComponent<SeekTarget>(),
			"W1 删除的组件经差集重挂载后应从 W2 实体移除");

		Console.WriteLine($"authoring-poc: W1→Baker→W2 端到端验证通过（score={match.Score}, alive={match.AliveEnemies}）");
	}

	private static int CountEntities(EcsWorld world) => world.Store.Entities.Count();
}
