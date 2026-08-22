// SPDX-License-Identifier: MIT
// AuthoringWorld.cs —— W1 Authoring 世界容器（P2.4 最小 W1 Core）
//
// 无 UI 的 Authoring 数据核心：对象表 + 层级 + 关系 + 原型 + Schema + 事务历史。
// 一切修改只能经 Apply(transaction)/Undo/Redo（UI 与 MCP 的共同入口）；
// ArtifactHash 是确定性指纹（SplitMix64），Undo/Redo 后必须完全恢复（P2.4 门禁）。
//
// 事务执行细节见 partial 文件 TransactionExecution.cs。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Baize.Authoring;

public sealed partial class AuthoringWorld
{
	private readonly Dictionary<StableId, AuthoringObject> _objects = new();
	private readonly Dictionary<StableId, List<StableId>> _children = new();
	private readonly Dictionary<StableId, ulong> _objectVersions = new();
	private readonly List<AppliedTransaction> _undoStack = new();
	private readonly List<AppliedTransaction> _redoStack = new();

	internal ulong _nextId = 1;
	internal ulong _version;

	public AuthoringWorld(AuthoringSchema schema)
	{
		Schema = schema ?? throw new ArgumentNullException(nameof(schema));
	}

	/// <summary>组件 Schema 注册表（对象携带哪些组件由它解释）。</summary>
	public AuthoringSchema Schema { get; }

	/// <summary>全局版本号：每次成功的事务/Undo/Redo 都推进它（Baker 脏检查的基准）。</summary>
	public ulong Version => _version;

	/// <summary>全部对象（无特定顺序；确定性输出请自行按 Id 排序）。</summary>
	public IReadOnlyCollection<AuthoringObject> Objects => _objects.Values;

	public int ObjectCount => _objects.Count;

	public bool CanUndo => _undoStack.Count > 0;
	public bool CanRedo => _redoStack.Count > 0;

	// —— 只读查询 ——

	public bool Exists(StableId id) => _objects.ContainsKey(id);

	public AuthoringObject? Find(StableId id) => _objects.TryGetValue(id, out var obj) ? obj : null;

	/// <summary>按 Id 取必需对象；不存在抛清晰异常。</summary>
	public AuthoringObject Require(StableId id) =>
		Find(id) ?? throw new KeyNotFoundException($"W1 对象不存在：{id}");

	/// <summary>按名字找第一个同名对象（名字可重复，仅作人读辅助；逻辑引用一律用 Id）。</summary>
	public AuthoringObject? FindByName(string name) =>
		_objects.Values.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.Ordinal));

	/// <summary>子对象 Id 列表（插入顺序；roots 传 StableId.None）。</summary>
	public IReadOnlyList<StableId> ChildrenOf(StableId id) =>
		_children.TryGetValue(id, out var list) ? list.ToArray() : Array.Empty<StableId>();

	/// <summary>是否为指定对象的祖先（含相等）——Reparent 防环用。</summary>
	public bool IsAncestorOrSelf(StableId ancestor, StableId id)
	{
		StableId current = id;
		var guard = new HashSet<StableId>();
		while (!current.IsNone)
		{
			if (current == ancestor) return true;
			if (!guard.Add(current)) return false;   // 数据损坏防死循环
			if (!_objects.TryGetValue(current, out var obj)) return false;
			current = obj.ParentId;
		}
		return false;
	}

	// —— Id 分配 ——

	/// <summary>分配一个稳定 Id（纯计数器递增；放弃使用只是留空洞，不影响一致性）。</summary>
	public StableId AllocateId()
	{
		if (_nextId >= ulong.MaxValue) throw new InvalidOperationException("StableId 空间已耗尽，无法再分配新 Id");
		var id = new StableId(_nextId);
		_nextId++;
		return id;
	}

	/// <summary>分配连续 count 个 Id，返回首个。</summary>
	public StableId AllocateIds(int count)
	{
		if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
		if (_nextId > ulong.MaxValue - (ulong)count) throw new InvalidOperationException("StableId 空间不足以分配连续段");
		var first = new StableId(_nextId);
		_nextId += (ulong)count;
		return first;
	}

	/// <summary>加载场景后直接设定计数器（调用方负责校验大于全部已加载 Id 且可继续分配）。</summary>
	internal void SetNextId(ulong value) => _nextId = value;

	/// <summary>清空 undo/redo 历史（装载场景后建立干净基线——历史不持久化契约）。</summary>
	internal void ClearHistory()
	{
		_undoStack.Clear();
		_redoStack.Clear();
	}
	internal ulong CurrentNextId => _nextId;

	// —— Prefab（原型）语义 ——

	/// <summary>
	/// 解析对象的有效组件集：沿原型链合并（近端覆盖远端），
	/// 本地覆盖（overrides）表示"此组件由本对象说了算"——含显式删除（override 但本地无值）。
	/// </summary>
	public Dictionary<Type, object> ResolveEffectiveComponents(AuthoringObject obj)
	{
		return ResolveChain(obj, new HashSet<StableId>());
	}

	private Dictionary<Type, object> ResolveChain(AuthoringObject obj, HashSet<StableId> visited)
	{
		if (!visited.Add(obj.Id))
		{
			throw new InvalidOperationException($"原型链出现环：对象 {obj.Id}（{obj.Name}）");
		}

		var result = new Dictionary<Type, object>();
		if (obj.PrototypeId is { } prototypeId)
		{
			result = ResolveChain(Require(prototypeId), visited);

			// 本地显式删除：override 记录了该类型、但本地没有组件值
			foreach (string typeName in obj._overrides)
			{
				if (Schema.TryGetByName(typeName, out var schema) &&
					!obj._components.ContainsKey(schema.ComponentType))
				{
					result.Remove(schema.ComponentType);
				}
			}
		}

		foreach (var pair in obj._components)
		{
			result[pair.Key] = pair.Value;
		}
		return result;
	}

	/// <summary>单个组件的解释结果（Prefab override 可查询可解释——P2.4 门禁）。</summary>
	public sealed record OverrideExplanation(string ComponentTypeName, string Source, string Detail);

	/// <summary>
	/// 解释一个对象所有组件的来源：local（本地值/本地覆盖）、inherited（继承自原型）、
	/// removed-override（相对原型显式删除）。按组件类型名排序。
	/// </summary>
	public IReadOnlyList<OverrideExplanation> ExplainComponents(StableId id)
	{
		var obj = Require(id);
		var explanations = new List<OverrideExplanation>();
		var effective = ResolveEffectiveComponents(obj);

		var names = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var type in effective.Keys) names.Add(Schema.Get(type).TypeName);
		foreach (var typeName in obj._overrides) names.Add(typeName);

		foreach (var typeName in names)
		{
			bool hasLocal = Schema.TryGetByName(typeName, out var schema) &&
				obj._components.ContainsKey(schema.ComponentType);

			if (hasLocal)
			{
				string detail = $"本地值 {JsonText(schema!, obj._components[schema!.ComponentType])}";
				if (obj.PrototypeId is { } pid && Find(pid) is { } proto &&
					proto._components.TryGetValue(schema!.ComponentType, out var protoValue))
				{
					detail += $"（原型 {pid} 值 {JsonText(schema, protoValue)}）";
				}
				explanations.Add(new OverrideExplanation(typeName, "local", detail));
			}
			else if (obj._overrides.Contains(typeName))
			{
				explanations.Add(new OverrideExplanation(typeName, "removed-override", "相对原型显式删除"));
			}
			else
			{
				string detail = obj.PrototypeId is { } inheritedFrom
					? $"继承自 {inheritedFrom}：{JsonText(Schema.GetByName(typeName), effective[Schema.GetByName(typeName).ComponentType])}"
					: "默认";
				explanations.Add(new OverrideExplanation(typeName, "inherited", detail));
			}
		}
		return explanations;
	}

	// —— 确定性 Artifact Hash ——

	/// <summary>
	/// 场景指纹：SplitMix64 混合全部权威数据（对象按 Id 序、组件按类型名序、
	/// 字段按声明序、关系按 (类型,目标) 序、名字/覆盖集按字节序）。
	/// 同一数据永远得到同一 hash；Undo/Redo 后必须完全恢复（门禁）。
	/// </summary>
	public ulong ComputeArtifactHash()
	{
		ulong hash = 0;
		hash = Mix(hash, _nextId);
		hash = Mix(hash, (ulong)_objects.Count);

		foreach (var id in SortedIds())
		{
			var obj = _objects[id];
			hash = Mix(hash, id.Value);
			hash = Mix(hash, obj.ParentId.Value);
			hash = Mix(hash, obj.PrototypeId?.Value ?? 0);
			hash = MixString(hash, obj.Name);

			foreach (var typeName in obj._overrides.OrderBy(n => n, StringComparer.Ordinal))
			{
				hash = MixString(hash, "override:" + typeName);
			}

			foreach (var pair in this.SortedComponents(obj))
			{
				var schema = Schema.Get(pair.Key);
				hash = MixString(hash, schema.TypeName);
				for (int index = 0; index < schema.Fields.Count; index++)
				{
					object raw = schema.GetFieldRaw(pair.Value, index);
					hash = Mix(hash, MixFieldValue(schema.Fields[index].Kind, raw));
					if (schema.Fields[index].Kind == SchemaFieldKind.String)
					{
							hash = MixString(hash, raw as string);   // null 由 MixString 归一
					}
				}
			}

			foreach (var relation in obj._relations
				.OrderBy(r => r.RelationType, StringComparer.Ordinal)
				.ThenBy(r => r.TargetId))
			{
				hash = MixString(hash, "rel:" + relation.RelationType);
				hash = Mix(hash, relation.TargetId.Value);
			}
		}
		return hash;
	}

	private IEnumerable<StableId> SortedIds()
	{
		var ids = new List<StableId>(_objects.Keys);
		ids.Sort();
		return ids;
	}

	private static ulong MixFieldValue(SchemaFieldKind kind, object raw) => kind switch
	{
		SchemaFieldKind.Int => (ulong)(int)raw,
		SchemaFieldKind.Long => unchecked((ulong)(long)raw),
		SchemaFieldKind.UInt => (uint)raw,
		SchemaFieldKind.ULong => (ulong)raw,
		SchemaFieldKind.Float => unchecked((ulong)BitConverter.DoubleToInt64Bits((double)(float)raw)),
		SchemaFieldKind.Double => unchecked((ulong)BitConverter.DoubleToInt64Bits((double)raw)),
		SchemaFieldKind.Bool => (bool)raw ? 1UL : 0UL,
		SchemaFieldKind.Enum => MixEnumRaw(raw),   // ulong 底层枚举可能超 long.MaxValue，保留位模式
		SchemaFieldKind.String => 0UL,   // 字符串由 MixString 另行混入
		_ => 0UL,
	};

	/// <summary>
	/// 枚举按底层类型取原始位模式（装箱枚举不能直接拆箱）：
	/// - ulong 底层走 IConvertible.ToUInt64（高位值不经 Int64 中转，不溢出）；
	/// - 其余（含负值有符号枚举）先 ToInt64 再 unchecked 转换，保留补码位模式。
	/// </summary>
	private static ulong MixEnumRaw(object raw)
	{
		var underlying = Enum.GetUnderlyingType(raw.GetType());
		return underlying == typeof(ulong)
			? Convert.ToUInt64(raw, System.Globalization.CultureInfo.InvariantCulture)
			: unchecked((ulong)Convert.ToInt64(raw));
	}

	internal static ulong Mix(ulong hash, ulong value)
	{
		unchecked
		{
			hash += value + 0x9E3779B97F4A7C15UL;
			hash = (hash ^ (hash >> 30)) * 0xBF58476D1CE4E5B9UL;
			hash = (hash ^ (hash >> 27)) * 0x94D049BB133111EBUL;
			return hash ^ (hash >> 31);
		}
	}

	internal static ulong MixString(ulong hash, string? text)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);   // null 归一为空串（与 Schema 读出口一致）
		hash = Mix(hash, (ulong)bytes.LongLength);
		int offset = 0;
		for (; offset + 8 <= bytes.Length; offset += 8)
		{
			// 固定小端字节序：hash 跨平台（大小端）稳定，不依赖宿主
			hash = Mix(hash, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8)));
		}
		if (offset < bytes.Length)
		{
			ulong tail = 0;
			for (int index = offset; index < bytes.Length; index++)
			{
				tail |= (ulong)bytes[index] << ((index - offset) * 8);
			}
			hash = Mix(hash, tail);
		}
		return hash;
	}

	private static string JsonText(IComponentSchema schema, object value)
	{
		var element = schema.ToJson(value);
		return element.GetRawText();
	}

	// —— 版本（Baker 脏检查） ——

	internal ulong ObjectVersion(StableId id) =>
		_objectVersions.TryGetValue(id, out ulong version) ? version : 0;

	internal void TouchObject(StableId id) => _objectVersions[id] = _version;

	internal void ForgetObject(StableId id) => _objectVersions.Remove(id);
}
