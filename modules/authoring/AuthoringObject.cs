// SPDX-License-Identifier: MIT
// AuthoringObject.cs —— W1 对象（P2.4）
//
// Object ≈ Entity（宪法定案）：对象是"人类能理解的 Entity"——若干事实在同一个稳定 Id 上的组合。
// 对象不承载行为（行为 = W2 System）；W1 只存数据与结构（层级/关系/原型）。
//
// 可变性约定：全部 internal set——外部只能经事务修改（UI 与 MCP 的唯一修改入口）。

using System;
using System.Collections.Generic;

namespace Baize.Authoring;

/// <summary>对象间关系（一等公民，非父子树）：from 对象 → 关系类型 → to 对象。</summary>
public readonly record struct AuthoringRelation(string RelationType, StableId TargetId);

/// <summary>
/// W1 Authoring 对象：稳定身份 + 名字 + 层级 + 组件集 + 关系 + 原型引用。
/// 引用一律指向 StableId——Rename/Reparent 不影响任何引用（P2.4 门禁）。
/// </summary>
public sealed class AuthoringObject
{
	internal AuthoringObject(StableId id, string name)
	{
		Id = id;
		_name = name;
	}

	public StableId Id { get; }

	/// <summary>显示名（可改名；引用不依赖它，重名合法——查找按 Id）。</summary>
	public string Name => _name;
	internal string _name;

	/// <summary>父对象（StableId.None = 根）。</summary>
	public StableId ParentId { get; internal set; }

	/// <summary>原型（Prefab）引用；null = 非实例。</summary>
	public StableId? PrototypeId { get; internal set; }

	/// <summary>组件值（装箱 struct，按 CLR 类型索引）。读取用，修改走事务。</summary>
	public IReadOnlyDictionary<Type, object> Components => _components;
	internal readonly Dictionary<Type, object> _components = new();

	/// <summary>出边关系列表（from = 本对象）。读取用，修改走事务。</summary>
	public IReadOnlyList<AuthoringRelation> Relations => _relations;
	internal readonly List<AuthoringRelation> _relations = new();

	/// <summary>
	/// 本地覆盖的组件类型全名（Prefab override 记录）。
	/// 含"本地显式删除"——本地覆盖但 Components 里没有 = 显式无此组件。
	/// </summary>
	public IReadOnlyCollection<string> OverriddenComponents => _overrides;
	internal readonly HashSet<string> _overrides = new(StringComparer.Ordinal);
}
