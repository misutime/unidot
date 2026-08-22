// SPDX-License-Identifier: MIT
// AuthoringTransaction.cs —— 事务与原子操作定义（P2.4）
//
// 事务是 W1 的唯一修改入口：UI 和 MCP 构造同一组 AuthoringOp（门禁"同一操作产生相同事务"）。
// op 是纯数据 record——可相等比较、可序列化、可重放（Redo 直接重放 ops）。
//
// 组件值的统一中间表示 = JsonElement（经 Schema 转换）：
// UI 的强类型 struct 与 MCP 的 JSON 在 op 层面收敛为同一种数据。

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Baize.Authoring;

/// <summary>原子操作基类。所有 op 都是纯数据 record。</summary>
public abstract record AuthoringOp;

/// <summary>创建对象。<paramref name="Id"/> 为 None 时 Apply 自动分配（实际 Id 记入 diff）。</summary>
public sealed record CreateObjectOp(StableId Id, string Name, StableId ParentId) : AuthoringOp;

/// <summary>删除对象（级联删除整棵子树；undo 按快照恢复）。</summary>
public sealed record DeleteObjectOp(StableId Id) : AuthoringOp;

public sealed record RenameObjectOp(StableId Id, string NewName) : AuthoringOp;

/// <summary>改父级。新父是本对象子孙会被预检拒绝（防环）。</summary>
public sealed record ReparentObjectOp(StableId Id, StableId NewParentId) : AuthoringOp;

/// <summary>
/// 添加组件。手写相等按 GetRawText 比较（词法级）——键序/空白不同的等价 JSON
/// 由 <see cref="AuthoringTransaction.Canonicalize"/> 在执行入口收敛为同一形态。
/// </summary>
public sealed record AddComponentOp(StableId Id, string ComponentType, JsonElement Value) : AuthoringOp
{
	public bool Equals(AddComponentOp? other) =>
		other is not null
		&& Id == other.Id
		&& ComponentType == other.ComponentType
		&& string.Equals(Value.GetRawText(), other.Value.GetRawText(), StringComparison.Ordinal);

	public override int GetHashCode() =>
		HashCode.Combine(Id, ComponentType, Value.GetRawText());
}

public sealed record RemoveComponentOp(StableId Id, string ComponentType) : AuthoringOp;

public sealed record SetComponentOp(StableId Id, string ComponentType, JsonElement Value) : AuthoringOp
{
	public bool Equals(SetComponentOp? other) =>
		other is not null
		&& Id == other.Id
		&& ComponentType == other.ComponentType
		&& string.Equals(Value.GetRawText(), other.Value.GetRawText(), StringComparison.Ordinal);

	public override int GetHashCode() =>
		HashCode.Combine(Id, ComponentType, Value.GetRawText());
}

public sealed record AddRelationOp(StableId Id, string RelationType, StableId TargetId) : AuthoringOp;
public sealed record RemoveRelationOp(StableId Id, string RelationType, StableId TargetId) : AuthoringOp;

/// <summary>设置原型引用；PrototypeId 为 None 表示清除（实例退化为普通对象，保留本地值）。</summary>
public sealed record SetPrototypeOp(StableId Id, StableId PrototypeId) : AuthoringOp;

/// <summary>
/// 有序原子操作列表：一次 Apply 要么全部生效，要么全部不生效。
/// 构造方式不限（C# 链式 / MCP 从 JSON 反序列化）——相同逻辑操作应构造出相同的 Ops 列表。
/// </summary>
public sealed class AuthoringTransaction
{
	private readonly List<AuthoringOp> _ops = new();

	/// <summary>操作列表（只读快照视图）。</summary>
	public IReadOnlyList<AuthoringOp> Ops => _ops;

	public int Count => _ops.Count;

	/// <summary>追加一个原始 op（MCP/序列化路径）。</summary>
	public void Add(AuthoringOp op) => _ops.Add(op ?? throw new ArgumentNullException(nameof(op)));

	// —— C# 便捷链式构造（内部仍只是 Add 对应 op） ——

	public AuthoringTransaction Create(StableId id, string name, StableId parent = default)
	{
		Add(new CreateObjectOp(id, name, parent));
		return this;
	}

	public AuthoringTransaction Delete(StableId id)
	{
		Add(new DeleteObjectOp(id));
		return this;
	}

	public AuthoringTransaction Rename(StableId id, string newName)
	{
		Add(new RenameObjectOp(id, newName));
		return this;
	}

	public AuthoringTransaction Reparent(StableId id, StableId newParent)
	{
		Add(new ReparentObjectOp(id, newParent));
		return this;
	}

	/// <summary>添加组件（强类型便捷：struct 经 Schema 序列化为 JSON）。</summary>
	public AuthoringTransaction AddComponent<T>(StableId id, in T value, AuthoringSchema schema) where T : struct
	{
		var componentSchema = schema.Get(typeof(T));
		Add(new AddComponentOp(id, componentSchema.TypeName, componentSchema.ToJson(value)));
		return this;
	}

	/// <summary>设置/覆盖组件（强类型便捷）。</summary>
	public AuthoringTransaction SetComponent<T>(StableId id, in T value, AuthoringSchema schema) where T : struct
	{
		var componentSchema = schema.Get(typeof(T));
		Add(new SetComponentOp(id, componentSchema.TypeName, componentSchema.ToJson(value)));
		return this;
	}

	public AuthoringTransaction RemoveComponent(string componentTypeName, StableId id)
	{
		Add(new RemoveComponentOp(id, componentTypeName));
		return this;
	}

	public AuthoringTransaction RemoveComponent<T>(StableId id, AuthoringSchema schema) where T : struct =>
		RemoveComponent(schema.Get(typeof(T)).TypeName, id);
	public AuthoringTransaction SetPrototype(StableId id, StableId prototypeId)
	{
		Add(new SetPrototypeOp(id, prototypeId));
		return this;
	}

	public AuthoringTransaction AddRelation(StableId id, string relationType, StableId target)
	{
		Add(new AddRelationOp(id, relationType, target));
		return this;
	}

	public AuthoringTransaction RemoveRelation(StableId id, string relationType, StableId target)
	{
		Add(new RemoveRelationOp(id, relationType, target));
		return this;
	}

	/// <summary>
	/// 返回规范化副本：Add/Set 的组件值经 Schema 读入后重新序列化（键序/空白统一），
	/// 且副本持有独立 JsonElement（与来源 JsonDocument 生命周期解耦，Undo/Redo 存档安全）。
	/// UI 强类型路径与 MCP 原始 JSON 路径的同一逻辑操作经此收敛为完全相同的 op 序列（门禁基础）。
	/// 组件类型未注册或 JSON 非法时抛出——调用方（Apply 入口）在改动世界之前即失败。
	/// </summary>
	public AuthoringTransaction Canonicalize(AuthoringSchema schema)
	{
		if (schema is null) throw new ArgumentNullException(nameof(schema));
		var canonical = new AuthoringTransaction();
		foreach (AuthoringOp op in _ops)
		{
			canonical._ops.Add(op switch
			{
				AddComponentOp add => add with { Value = CanonicalValue(add.Value, add.ComponentType, schema) },
				SetComponentOp set => set with { Value = CanonicalValue(set.Value, set.ComponentType, schema) },
				_ => op,
			});
		}
		return canonical;
	}

	private static JsonElement CanonicalValue(JsonElement value, string componentType, AuthoringSchema schema)
	{
		try
		{
			return schema.GetByName(componentType).ToJson(schema.GetByName(componentType).ReadJson(value));
		}
		catch (Exception ex) when (ex is not KeyNotFoundException)
		{
			throw new FormatException(
				$"组件 '{componentType}' 的值无法按 Schema 解析：{value.GetRawText()}（{ex.Message}）", ex);
		}
	}
}
