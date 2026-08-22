// SPDX-License-Identifier: MIT
// SceneBaker.cs —— W1 → W2 烘焙（P2.4 最小 Baker）
//
// IRuntimeSceneSource 是 Baker 的产出契约：W2 侧消费它把 Authoring 数据 spawn 进 EcsWorld，
// 不需要改动 Baize.Ecs（"不改 W2 契约"）。
//
// 组件类型一份两用：W1 存的装箱 struct 就是 W2 的 IComponent/ITag——直通烘焙零映射。
// Prefab 在烘焙时解析：BakedObject 拿到的是有效组件集（原型链合并 + 本地覆盖）。
//
// 增量重烘（门禁"单组件修改只重烘相关对象"）：
// AuthoringWorld 记录每对象版本；BakedScene 记录上次烘焙版本——
// 只重烘"新增/修改/删除"的对象，未变对象的 BakedObject 实例原样保留。

using System;
using System.Collections.Generic;
using Baize.Ecs;
using System.Linq;
namespace Baize.Authoring;

/// <summary>
/// 烘焙产物的只读视图：一组带稳定 Id 的对象 + 可直接写入 W2 的组件值。
/// W2 消费端按此接口装载场景（spawn 实体、解析父子与关系）。
/// </summary>
public interface IRuntimeSceneSource
{
	/// <summary>全部烘焙对象（无特定顺序）。</summary>
	IReadOnlyCollection<BakedObject> Objects { get; }

	/// <summary>按稳定 Id 查找烘焙对象。</summary>
	BakedObject? Find(StableId id);
}

/// <summary>单个烘焙对象：稳定 Id + 名字 + 层级父 + 有效组件值（装箱 struct，W2 可直接使用）。</summary>
/// <summary>
/// 单个烘焙对象：稳定 Id + 名字 + 层级父 + 有效组件值（装箱 struct，W2 可直接使用）+ 出边关系。
/// </summary>
public sealed class BakedObject
{
	public StableId Id { get; }
	public string Name { get; }
	public StableId ParentId { get; }

	/// <summary>组件值：CLR 类型 → 装箱 struct。标签（无字段组件）同样在此。</summary>
	public IReadOnlyDictionary<Type, object> Components { get; }

	/// <summary>出边关系（目标为 StableId；映射到 W2 由消费端决定）。</summary>
	public IReadOnlyList<AuthoringRelation> Relations { get; }

	internal BakedObject(StableId id, string name, StableId parentId,
		Dictionary<Type, object> components, List<AuthoringRelation> relations)
	{
		Id = id;
		Name = name;
		ParentId = parentId;
		Components = components;
		Relations = relations;
	}
}
/// <summary>IRuntimeSceneSource 的可变实现：支持增量更新（只重烘脏对象）。</summary>
public sealed class BakedScene : IRuntimeSceneSource
{
	private readonly Dictionary<StableId, BakedObject> _objects = new();
	private readonly Dictionary<StableId, ulong> _bakedVersions = new();

	public IReadOnlyCollection<BakedObject> Objects => _objects.Values;

	public BakedObject? Find(StableId id) => _objects.TryGetValue(id, out var baked) ? baked : null;

	public int Count => _objects.Count;

	/// <summary>替换/加入一个烘焙对象（记录其来源版本）。</summary>
	internal void Put(BakedObject obj, ulong sourceVersion)
	{
		_objects[obj.Id] = obj;
		_bakedVersions[obj.Id] = sourceVersion;
	}

	/// <summary>移除对象（源里已删除）。</summary>
	internal bool Remove(StableId id)
	{
		bool removed = _objects.Remove(id);
		removed |= _bakedVersions.Remove(id);
		return removed;
	}

	internal bool IsUpToDate(StableId id, ulong currentVersion) =>
		_bakedVersions.TryGetValue(id, out ulong baked) && baked == currentVersion;

	internal IEnumerable<StableId> StaleIds(AuthoringWorld world)
	{
		foreach (var existing in _objects.Keys.ToList())
		{
			if (!world.Exists(existing))
			{
				yield return existing;   // 已被删除
			}
		}
	}

	internal void Clear()
	{
		_objects.Clear();
		_bakedVersions.Clear();
	}
}

/// <summary>
/// W1 → W2 场景烘焙器。全量 Bake 或增量 Bake(world, scene) 二选一；
/// LastBakedObjectCount 记录最近一次实际重烘的对象数（增量效果的直接证据）。
/// </summary>
public sealed class SceneBaker
{
	private readonly AuthoringSchema _schema;

	/// <summary>最近一次 Bake 实际重新烘焙的对象数（0 = 全部最新）。</summary>
	public int LastBakedObjectCount { get; private set; }

	public SceneBaker(AuthoringSchema schema)
	{
		_schema = schema ?? throw new ArgumentNullException(nameof(schema));
	}

	/// <summary>全量烘焙为新场景。</summary>
	public BakedScene Bake(AuthoringWorld world)
	{
		var scene = new BakedScene();
		Bake(world, scene);
		return scene;
	}

	/// <summary>
	/// 增量烘焙进既有场景：只重烘版本变化的对象，删除已不存在的对象，
	/// 其余对象原样保留（引用不变——消费端无需重 spawn 未变的实体）。
	/// </summary>
	public void Bake(AuthoringWorld world, BakedScene scene)
	{
		int baked = 0;

		foreach (var obj in world.Objects)
		{
			ulong version = world.ObjectVersion(obj.Id);
			if (scene.IsUpToDate(obj.Id, version)) continue;

			scene.Put(BakeObject(world, obj), version);
			baked++;
		}

		foreach (StableId stale in scene.StaleIds(world))
		{
			scene.Remove(stale);
			baked++;
		}

		LastBakedObjectCount = baked;
	}

	private BakedObject BakeObject(AuthoringWorld world, AuthoringObject obj)
	{
		var effective = world.ResolveEffectiveComponents(obj);
		var components = new Dictionary<Type, object>(effective.Count);
		foreach (var pair in effective)
		{
			components[pair.Key] = pair.Value;   // 装箱 struct 直通；W2 写入时按需 Clone 语义安全
		}
		return new BakedObject(obj.Id, obj.Name, obj.ParentId, components,
			new List<AuthoringRelation>(obj._relations));
	}
}

/// <summary>
/// W2 消费端辅助：把烘焙场景 spawn 进 EcsWorld。
/// 放在 Authoring 是为了复用 Schema 解析；依赖 Baize.Ecs（W2）是刻意的单向依赖
/// （Authoring → Ecs），Ecs 不反向引用 Authoring。
/// </summary>
public static class RuntimeSceneSpawner
{
	/// <summary>
	/// 两遍式装载：第一遍创建全部实体并写组件；第二遍建立 StableId ↔ Entity 映射后，
	/// 由调用方继续解析跨对象引用（关系目标此时才全部存在）。
	/// </summary>
	public static Dictionary<StableId, Friflo.Engine.ECS.Entity> Spawn(
		this Baize.Ecs.EcsWorld world, IRuntimeSceneSource scene)
	{
		var map = new Dictionary<StableId, Friflo.Engine.ECS.Entity>();
		foreach (var baked in scene.Objects.OrderBy(o => o.Id))
		{
			var entity = world.SpawnNow(new RuntimeBundle(baked.Components));
			map[baked.Id] = entity;
		}
		return map;
	}

	/// <summary>
	/// 增量重挂载：对比旧/新烘焙对象，把差异同步到 W2 实体——
	/// 新增组件写入、共有组件覆盖、W1 已删除的组件/标签同步移除（保持 W2 与烘焙结果一致）。
	/// 同步直调 Entity API（编辑器操作，非系统热路径）；previous 为 null 时全量写入。
	/// </summary>
	public static void ApplyTo(this Friflo.Engine.ECS.Entity entity, BakedObject? previous, BakedObject current)
	{
		if (previous is not null)
		{
			foreach (var removedType in previous.Components.Keys.Where(t => !current.Components.ContainsKey(t)))
			{
				EntityMutator.Mutate(entity, removedType, null, EntityMutator.MutateKind.Remove);
			}
		}

		foreach (var pair in current.Components)
		{
			bool existed = previous is not null && previous.Components.ContainsKey(pair.Key);
			EntityMutator.Mutate(entity, pair.Key, pair.Value,
				existed ? EntityMutator.MutateKind.Replace : EntityMutator.MutateKind.Add);
		}
	}

	private sealed class RuntimeBundle : Baize.Ecs.IEntityBundle
	{
		private readonly IReadOnlyDictionary<Type, object> _components;

		public RuntimeBundle(IReadOnlyDictionary<Type, object> components)
		{
			_components = components;
		}

		public void Apply(in Baize.Ecs.EntityCommand entity)
		{
			foreach (var pair in _components)
			{
				Type type = pair.Key;
				object value = pair.Value;
				WriteComponent(entity, type, value);
			}
		}

		private static void WriteComponent(Baize.Ecs.EntityCommand entity, Type type, object value)
		{
			// 标签（无字段 ITag struct）走 AddTag；组件走泛型 Add。
			// 类型擦除点收敛在这一个方法：反射句柄按类型缓存，只在烘焙装载时发生，非热路径。
			if (typeof(Friflo.Engine.ECS.ITag).IsAssignableFrom(type))
			{
				object boxed = entity;   // 装箱副本；AddTag 的副作用经 owner 引用生效
				GetAddTagMethod(type).Invoke(boxed, null);
				return;
			}
			GetComponentWriter(type).Invoke(null, new[] { (object)entity, value });
		}

		private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.MethodInfo> TagMethods = new();
		private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.MethodInfo> ComponentWriters = new();

		private static System.Reflection.MethodInfo GetAddTagMethod(Type type) =>
			TagMethods.GetOrAdd(type, static t =>
				typeof(Baize.Ecs.EntityCommand).GetMethods()
					.Single(m => m.Name == nameof(Baize.Ecs.EntityCommand.AddTag) && m.IsGenericMethodDefinition)
					.MakeGenericMethod(t));

		private static System.Reflection.MethodInfo GetComponentWriter(Type type) =>
			ComponentWriters.GetOrAdd(type, static t =>
				typeof(RuntimeBundle)
					.GetMethod(nameof(AddComponentTyped), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
					.MakeGenericMethod(t));


		private static void AddComponentTyped<T>(in Baize.Ecs.EntityCommand entity, in T component)
			where T : struct, Friflo.Engine.ECS.IComponent
		{
			entity.Add(component);
		}
	}
}

/// <summary>
/// 类型擦除的 W2 实体变更点：标签（ITag）与组件（IComponent）经反射分派到
/// Entity 的 Add/Remove 泛型方法。句柄按 (类型, 操作) 缓存；仅用于场景装载/重挂载，非热路径。
/// </summary>
internal static class EntityMutator
{
	internal enum MutateKind { Add, Replace, Remove }

	private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type, MutateKind), System.Reflection.MethodInfo> Methods = new();

	internal static void Mutate(Friflo.Engine.ECS.Entity entity, Type type, object? value, MutateKind kind)
	{
		if (kind == MutateKind.Replace && !typeof(Friflo.Engine.ECS.ITag).IsAssignableFrom(type))
		{
			// Entity 无单组件 Set：先移除再添加（等价覆盖；标签无值无需 Replace）
			Mutate(entity, type, null, MutateKind.Remove);
			Mutate(entity, type, value, MutateKind.Add);
			return;
		}

		bool isTag = typeof(Friflo.Engine.ECS.ITag).IsAssignableFrom(type);
		var method = Methods.GetOrAdd((type, kind), static key =>
		{
			var (t, k) = key;
			bool tag = typeof(Friflo.Engine.ECS.ITag).IsAssignableFrom(t);
			string name = k switch
			{
				MutateKind.Remove => tag ? nameof(Friflo.Engine.ECS.Entity.RemoveTag) : nameof(Friflo.Engine.ECS.Entity.RemoveComponent),
				_ => tag ? nameof(Friflo.Engine.ECS.Entity.AddTag) : nameof(Friflo.Engine.ECS.Entity.AddComponent),
			};
			int expectedParams = k == MutateKind.Remove || tag ? 0 : 1;
			return typeof(Friflo.Engine.ECS.Entity)
				.GetMethods()
				.Where(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == expectedParams)
				.First()
				.MakeGenericMethod(t);
		});

		object boxed = entity;   // 装箱副本；副作用经内部 store 引用生效
		if (kind == MutateKind.Remove || isTag)
		{
			method.Invoke(boxed, null);   // Remove 与 AddTag/RemoveTag 均无参数
			return;
		}
		method.Invoke(boxed, new[] { value });
	}
}
