// SPDX-License-Identifier: MIT
// TestSupport.cs —— 验收共享构建器（Schema / 标准场景）

using System;
using System.Collections.Generic;
using Baize.Authoring;
using System.Text.Json;
using Shooter.Gameplay;

namespace AuthoringPoc.Tests;

internal static class TestSupport
{
	/// <summary>注册 shooter-gameplay 全部 [Component]（源生成产物，全局命名空间）。</summary>
	public static AuthoringSchema BuildSchema()
	{
		var schema = new AuthoringSchema();
		AuthoringSchemaRegistration.RegisterAll(schema);
		return schema;
	}

	public sealed record SceneIds(StableId Player, StableId Group, StableId Enemy1, StableId Enemy2);

	/// <summary>
	/// 标准确定性测试场景（显式 Id，两次构建 hash 相同）：
	/// o1 Player(0,0) 全套玩家组件；o2 EnemyGroup 空容器；o3 Enemy1(0,10)、o4 Enemy2(5,5) 挂在 o2 下。
	/// </summary>
	public static (AuthoringWorld World, SceneIds Ids) BuildScene(AuthoringSchema? schema = null)
	{
		schema ??= BuildSchema();
		var world = new AuthoringWorld(schema);
		StableId first = world.AllocateIds(4);
		var ids = new SceneIds(
			new StableId(first.Value),
			new StableId(first.Value + 1),
			new StableId(first.Value + 2),
			new StableId(first.Value + 3));

		var tx = new AuthoringTransaction();
		tx.Create(ids.Player, "Player");
		tx.AddComponent(ids.Player, new Position { X = 0f, Z = 0f }, schema);
		tx.AddComponent(ids.Player, new PreviousPosition(), schema);
		tx.AddComponent(ids.Player, new Velocity(), schema);
		tx.AddComponent(ids.Player, new PlayerInput(), schema);
		tx.AddComponent(ids.Player, new MoveSpeed { Value = 8f }, schema);
		tx.AddComponent(ids.Player, new WeaponConfig { CooldownSeconds = 0.3f, ProjectileSpeed = 30f }, schema);
		tx.AddComponent(ids.Player, new Cooldown(), schema);
		tx.AddComponent(ids.Player, new CollisionRadius { Value = 0.5f }, schema);
		tx.AddComponent(ids.Player, new PlayerFaction(), schema);   // 标签也是 W1 组件（无字段）
		world.Apply(tx);

		var txGroup = new AuthoringTransaction();
		txGroup.Create(ids.Group, "EnemyGroup");
		txGroup.Create(ids.Enemy1, "Enemy1", parent: ids.Group);
		AddEnemyComponents(txGroup, ids.Enemy1, schema, x: 0f, z: 10f, health: 30);
		txGroup.Create(ids.Enemy2, "Enemy2", parent: ids.Group);
		AddEnemyComponents(txGroup, ids.Enemy2, schema, x: 5f, z: 5f, health: 60);
		world.Apply(txGroup);

		return (world, ids);
	}

	private static void AddEnemyComponents(
		AuthoringTransaction tx, StableId id, AuthoringSchema schema, float x, float z, int health)
	{
		tx.AddComponent(id, new Position { X = x, Z = z }, schema);
		tx.AddComponent(id, new PreviousPosition { X = x, Z = z }, schema);
		tx.AddComponent(id, new Velocity(), schema);
		tx.AddComponent(id, new SeekTarget(), schema);
		tx.AddComponent(id, new MoveSpeed { Value = 3.5f }, schema);
		tx.AddComponent(id, new Health { Current = health, Max = health }, schema);
		tx.AddComponent(id, new CollisionRadius { Value = 0.5f }, schema);
		tx.AddComponent(id, new EnemyFaction(), schema);
	}

	/// <summary>MCP 路径模拟：直接用原始 op + JSON 构造事务（不经强类型便捷）。</summary>
	public static JsonElement Json(string text)
	{
		using var document = JsonDocument.Parse(text);
		return document.RootElement.Clone();
	}
}
