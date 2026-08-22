// SPDX-License-Identifier: MIT
// SchemaTests.cs —— Schema 注册与按名读写（生成器产物验证）

using System;
using System.Linq;
using Baize.Authoring;
using Shooter.Gameplay;

namespace AuthoringPoc.Tests;

internal static class SchemaTests
{
	public static void Run(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();

		check(schema.All.Count >= 12, $"应注册全部 shooter 组件（≥12），实际 {schema.All.Count}");

		var healthSchema = schema.GetByName("Shooter.Gameplay.Health");
		check(healthSchema.ComponentType == typeof(Health), "按类型名取回应是 Health");
		check(healthSchema.Fields.Select(f => f.Name).SequenceEqual(["Current", "Max"]),
			"Health 字段应按声明顺序 [Current, Max]");

		// 按名读写：模拟 MCP 侧"改字段"路径
		object boxed = healthSchema.CreateDefault();
		check(((Health)boxed).Current == 0 && ((Health)boxed).Max == 0, "默认值应为 0/0");
		healthSchema.SetFieldRaw(ref boxed, 0, 42);
		healthSchema.SetFieldRaw(ref boxed, 1, 100);
		check(((Health)boxed).Current == 42 && ((Health)boxed).Max == 100, "SetFieldRaw 应写入装箱实例");
		check((int)healthSchema.GetFieldRaw(boxed, 0) == 42, "GetFieldRaw 应回读字段值");

		// JSON 往返（持久化/MCP 的统一中间表示）
		var json = healthSchema.ToJson(boxed);
		check(json.GetProperty("Current").GetInt32() == 42, "ToJson 应含 Current=42");
		object reparsed = healthSchema.ReadJson(json);
		check(healthSchema.ValueEquals(boxed, reparsed), "JSON 往返后值应相等");
		check(!healthSchema.ValueEquals(boxed, healthSchema.CreateDefault()), "不同值不应误判相等");

		// 标签组件（无字段）也可注册与构造
		var factionSchema = schema.Get(typeof(PlayerFaction));
		check(factionSchema.Fields.Count == 0, "标签组件应无字段");
		check(factionSchema.ComponentType == typeof(PlayerFaction), "标签组件按 CLR 类型可查");

	Console.WriteLine($"authoring-poc: Schema 注册/按名读写验证通过（{schema.All.Count} 个组件）");

		RunUlongEnumHash(check);
	}

	// ulong 底层枚举：高位值（> long.MaxValue）是 hash 路径的边界情况
	[Flags]
	private enum BigMask : ulong
	{
		None = 0,
		Low = 1UL << 5,
		High = 0x8000_0000_0000_0000UL,
	}

	private struct BigMaskHolder
	{
		public BigMask Mask;
	}

	/// <summary>手写 Schema：与生成器产物同构，验证 ComponentSchemaBase 契约对 ulong 枚举的兼容性。</summary>
	private sealed class BigMaskHolderSchema : Baize.Authoring.ComponentSchemaBase
	{
		public BigMaskHolderSchema() : base(
			typeof(BigMaskHolder),
			new[] { new Baize.Authoring.SchemaField("Mask", typeof(BigMask), 0) })
		{
		}

		public override object CreateDefault() => default(BigMaskHolder);

		public override object GetFieldRaw(object component, int index) => index switch
		{
			0 => ((BigMaskHolder)component).Mask,
			_ => throw new ArgumentOutOfRangeException(nameof(index)),
		};

		public override void SetFieldRaw(ref object component, int index, object value)
		{
			if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
			var typed = (BigMaskHolder)component;
			typed.Mask = (BigMask)value;
			component = typed;
		}
	}

	/// <summary>P2 验证：ulong 底层枚举（含高位值）可安全参与 ArtifactHash 与 JSON 往返。</summary>
	private static void RunUlongEnumHash(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();

		// 手写 Schema 子类（生成器之外的合法路径）：验证 ulong 底层枚举字段
		schema.Register(new BigMaskHolderSchema());

		var world = new AuthoringWorld(schema);
		var id = world.AllocateId();
		var tx = new AuthoringTransaction();
		tx.Create(id, "BigMaskObj");
		world.Apply(tx);

		var setTx = new AuthoringTransaction();
		setTx.Add(new SetComponentOp(id, typeof(BigMaskHolder).FullName!,
			System.Text.Json.JsonDocument.Parse("{\"Mask\":\"High\"}").RootElement.Clone()));
		world.Apply(setTx);

		ulong hash = world.ComputeArtifactHash();   // 修复前此处抛 InvalidCastException
		check(hash != 0, "ulong 底层枚举应能参与 ArtifactHash");

		var setTx2 = new AuthoringTransaction();
		setTx2.Add(new SetComponentOp(id, typeof(BigMaskHolder).FullName!,
			System.Text.Json.JsonDocument.Parse("{\"Mask\":\"Low\"}").RootElement.Clone()));
		world.Apply(setTx2);
		check(world.ComputeArtifactHash() != hash, "不同枚举值应产生不同 hash");

		// JSON 往返：枚举写名字、读回按底层类型还原位模式
		var boxed = world.Require(id).Components[typeof(BigMaskHolder)];
		var maskValue = (BigMask)schema.Get(typeof(BigMaskHolder)).GetFieldRaw(boxed, 0);
		check(maskValue == BigMask.Low, $"JSON 往返后枚举应为 Low，实际 {maskValue}");

	Console.WriteLine("authoring-poc: ulong 枚举 hash/往返验证通过");

		RunNegativeEnumHash(check);
	}

	// 负值有符号枚举：hash 混入路径的另一个边界（补码位模式）
	private enum SignedState : int
	{
		Unknown = -1,
		Active = 1,
	}

	private struct SignedStateHolder
	{
		public SignedState State;
	}

	private sealed class SignedStateHolderSchema : Baize.Authoring.ComponentSchemaBase
	{
		public SignedStateHolderSchema() : base(
			typeof(SignedStateHolder),
			new[] { new Baize.Authoring.SchemaField("State", typeof(SignedState), 0) })
		{
		}

		public override object CreateDefault() => default(SignedStateHolder);

		public override object GetFieldRaw(object component, int index) => index switch
		{
			0 => ((SignedStateHolder)component).State,
			_ => throw new ArgumentOutOfRangeException(nameof(index)),
		};

		public override void SetFieldRaw(ref object component, int index, object value)
		{
			if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
			var typed = (SignedStateHolder)component;
			typed.State = (SignedState)value;
			component = typed;
		}
	}

	/// <summary>P2 验证：负值有符号枚举（Unknown=-1）参与 ArtifactHash 不抛溢出。</summary>
	private static void RunNegativeEnumHash(Action<bool, string> check)
	{
		var schema = TestSupport.BuildSchema();
		schema.Register(new SignedStateHolderSchema());

		var world = new AuthoringWorld(schema);
		var id = world.AllocateId();
		var tx = new AuthoringTransaction();
		tx.Create(id, "SignedObj");
		world.Apply(tx);

		var setTx = new AuthoringTransaction();
		setTx.Add(new SetComponentOp(id, typeof(SignedStateHolder).FullName!,
			System.Text.Json.JsonDocument.Parse("{\"State\":\"Unknown\"}").RootElement.Clone()));
		world.Apply(setTx);

		ulong negativeHash = world.ComputeArtifactHash();   // 回归点：-1 经 unchecked 补码混入，不抛 OverflowException
		check(negativeHash != 0, "负值有符号枚举应能参与 ArtifactHash");

		var flipTx = new AuthoringTransaction();
		flipTx.Add(new SetComponentOp(id, typeof(SignedStateHolder).FullName!,
			System.Text.Json.JsonDocument.Parse("{\"State\":\"Active\"}").RootElement.Clone()));
		world.Apply(flipTx);
		check(world.ComputeArtifactHash() != negativeHash, "不同枚举值应产生不同 hash");

		Console.WriteLine("authoring-poc: 负值有符号枚举 hash 验证通过");
	}
}
