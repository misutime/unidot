// SPDX-License-Identifier: MIT
// AuthoringSceneFile.cs —— W1 场景的确定性持久化（P2.4）
//
// Git 友好的稳定 JSON：
// - 对象按 StableId 升序、组件按类型全名序、关系按 (类型,目标) 序、覆盖集按字节序；
// - 字段顺序由 Schema 声明序决定、枚举写名字、浮点 round-trip——
//   同一数据永远得到同一文件字节；Save → Load → Save 逐字节相同。
// - nextId 随文件保存：加载后继续分配不会撞已有 Id。
//
// 事务历史不持久化（编辑会话内有效）；场景文件即 Artifact。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Baize.Authoring;

public static class AuthoringSceneFile
{
	public const string FormatName = "baize-scene";
	public const int FormatVersion = 1;

	/// <summary>保存 W1 世界为稳定 JSON 文件。</summary>
	public static void Save(AuthoringWorld world, string path)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		using var stream = File.Create(path);
		Save(world, stream);
	}

	public static void Save(AuthoringWorld world, Stream stream)
	{
		var options = new JsonWriterOptions { Indented = true };
		using var writer = new Utf8JsonWriter(stream, options);

		writer.WriteStartObject();
		writer.WriteString("format", FormatName);
		writer.WriteNumber("version", FormatVersion);
		writer.WriteNumber("nextId", world.CurrentNextId);

		writer.WriteStartArray("objects");
		foreach (var id in SortedIds(world))
		{
			var obj = world.Require(id);
			writer.WriteStartObject();
			writer.WriteNumber("id", id.Value);
			writer.WriteString("name", obj.Name);
			writer.WriteNumber("parent", obj.ParentId.Value);
			if (obj.PrototypeId is { } prototype)
			{
				writer.WriteNumber("prototype", prototype.Value);
			}

			writer.WriteStartObject("components");
			foreach (var pair in world.SortedComponents(obj))
			{
				var schema = world.Schema.Get(pair.Key);
				writer.WritePropertyName(schema.TypeName);
				schema.WriteJson(writer, pair.Value);
			}
			writer.WriteEndObject();

			writer.WriteStartArray("relations");
			foreach (var relation in world.SortRelations(obj))
			{
				writer.WriteStartObject();
				writer.WriteString("type", relation.RelationType);
				writer.WriteNumber("target", relation.TargetId.Value);
				writer.WriteEndObject();
			}
			writer.WriteEndArray();

			writer.WriteStartArray("overrides");
			foreach (string typeName in world.SortedOverrides(obj))
			{
				writer.WriteStringValue(typeName);
			}
			writer.WriteEndArray();

			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		writer.WriteEndObject();
		writer.Flush();
	}

	/// <summary>从稳定 JSON 文件加载为新世界（undo/redo 历史不恢复）。</summary>
	public static AuthoringWorld Load(string path, AuthoringSchema schema)
	{
		if (schema is null) throw new ArgumentNullException(nameof(schema));
		using var document = JsonDocument.Parse(File.ReadAllBytes(path));
		return FromJson(document.RootElement, schema);
	}

	public static AuthoringWorld Load(Stream stream, AuthoringSchema schema)
	{
		if (schema is null) throw new ArgumentNullException(nameof(schema));
		using var document = JsonDocument.Parse(stream);
		return FromJson(document.RootElement, schema);
	}

	internal static AuthoringWorld FromJson(JsonElement root, AuthoringSchema schema)
	{
		string? format = root.TryGetProperty("format", out var formatElement) ? formatElement.GetString() : null;
		if (!string.Equals(format, FormatName, StringComparison.Ordinal))
		{
			throw new InvalidDataException($"不是 {FormatName} 场景文件：format='{format}'");
		}
		int version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetInt32() : 0;
		if (version > FormatVersion)
		{
			throw new InvalidDataException($"场景格式版本过新：文件 v{version}，本程序支持到 v{FormatVersion}");
		}

		var world = new AuthoringWorld(schema);

		// 两阶段加载：先创建全部对象，再恢复结构与数据——
		// Save 按 Id 排序输出，但合法世界允许前向引用（o1 的父/原型/关系可指向 o2），
		// 单遍加载会对"引用尚未定义对象"的场景误报。
		var pending = new List<PendingObject>();
		var creationTx = new AuthoringTransaction();
		foreach (var objectElement in root.GetProperty("objects").EnumerateArray())
		{
			var id = new StableId(objectElement.GetProperty("id").GetUInt64());
			string name = objectElement.GetProperty("name").GetString()
				?? throw new InvalidDataException($"对象 {id} 的 name 缺失");
			if (id.IsNone)
			{
				throw new InvalidDataException("对象的 id 不能为 0");
			}

			creationTx.Add(new CreateObjectOp(id, name, StableId.None));

			var parent = new StableId(objectElement.GetProperty("parent").GetUInt64());
			StableId? prototype = objectElement.TryGetProperty("prototype", out var protoElement)
				? new StableId(protoElement.GetUInt64())
				: null;

			var components = new List<(string TypeName, JsonElement Value)>();
			foreach (var componentProperty in objectElement.GetProperty("components").EnumerateObject())
			{
				components.Add((componentProperty.Name, componentProperty.Value.Clone()));
			}

			var relations = new List<AuthoringRelation>();
			foreach (var relationElement in objectElement.GetProperty("relations").EnumerateArray())
			{
				relations.Add(new AuthoringRelation(
					relationElement.GetProperty("type").GetString()
						?? throw new InvalidDataException($"对象 {id} 有缺 type 的关系"),
					new StableId(relationElement.GetProperty("target").GetUInt64())));
			}

			pending.Add(new PendingObject(id, parent, prototype, components, relations));
		}
		world.Apply(creationTx);

		// 第二遍：恢复层级、原型、组件、关系（此时全部对象已存在，环校验基于完整图）
		foreach (var item in pending)
		{
			var structureTx = new AuthoringTransaction();
			if (!item.ParentId.IsNone)
			{
				structureTx.Reparent(item.Id, item.ParentId);
			}
			foreach (var (typeName, value) in item.Components)
			{
				structureTx.Add(new AddComponentOp(item.Id, typeName, value));
			}
			foreach (var relation in item.Relations)
			{
				structureTx.Add(new AddRelationOp(item.Id, relation.RelationType, relation.TargetId));
			}
			// 原型必须最后设置：先设原型再添加组件会被 MarkLocalOverride 误标为本地覆盖
			if (item.PrototypeId is { } prototypeValue)
			{
				structureTx.SetPrototype(item.Id, prototypeValue);
			}
			world.Apply(structureTx);

			// overrides 是落盘的权威数据（派生记录）：以文件内容整体覆盖恢复
			var restored = world.Require(item.Id);
			restored._overrides.Clear();
			if (OverridesOf(root, item.Id) is { } overrides)   // 无条件恢复（含清除原型后的残留记录）
			{
				foreach (var overrideElement in overrides.EnumerateArray())
				{
					restored._overrides.Add(overrideElement.GetString()
						?? throw new InvalidDataException($"对象 {item.Id} 有空 override 记录"));
				}
			}
		}
		// nextId 必须严格大于全部对象 Id（损坏文件拒绝加载，绝不按数值循环推进）
		if (root.TryGetProperty("nextId", out var nextIdElement))
		{
			ulong savedNextId = nextIdElement.GetUInt64();
			ulong maxObjectId = pending.Count == 0 ? 0 : pending.Max(p => p.Id.Value);   // 空场景合法
			if (savedNextId == 0 || savedNextId >= ulong.MaxValue || savedNextId <= maxObjectId)
			{
				throw new InvalidDataException(
					$"nextId 非法：{savedNextId}（必须大于最大对象 Id {maxObjectId} 且非零）");
			}
			world.SetNextId(savedNextId);
		}
		else
		{
			throw new InvalidDataException("场景文件缺少 nextId 字段");
		}
		world.ClearHistory();   // 装载建立干净基线：历史不持久化，且防止 Undo 破坏恢复的计数器
		return world;
	}

	private static System.Collections.Generic.List<StableId> SortedIds(AuthoringWorld world)
	{
		var ids = new System.Collections.Generic.List<StableId>(world.ObjectCount);
		foreach (var obj in world.Objects)
		{
			ids.Add(obj.Id);
		}
		ids.Sort();
		return ids;
	}

	private static JsonElement? OverridesOf(JsonElement root, StableId id)
	{
		foreach (var objectElement in root.GetProperty("objects").EnumerateArray())
		{
			if (new StableId(objectElement.GetProperty("id").GetUInt64()) != id) continue;
			return objectElement.TryGetProperty("overrides", out var overridesElement)
				? overridesElement
				: null;
		}
		return null;
	}

	/// <summary>第一遍收集的结构信息：对象创建后延后恢复的引用型数据。</summary>
	private sealed record PendingObject(
		StableId Id,
		StableId ParentId,
		StableId? PrototypeId,
		System.Collections.Generic.List<(string TypeName, JsonElement Value)> Components,
		System.Collections.Generic.List<AuthoringRelation> Relations);
}
internal static class AuthoringWorldSerializationExtensions
{
	/// <summary>组件按类型全名排序（序列化与 hash 共用同一确定性顺序）。</summary>
	public static System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<Type, object>>
		SortedComponents(this AuthoringWorld world, AuthoringObject obj)
	{
		var pairs = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Type, object>>(obj.Components);
		pairs.Sort((a, b) => string.CompareOrdinal(a.Key.FullName, b.Key.FullName));
		return pairs;
	}

	public static System.Collections.Generic.IEnumerable<AuthoringRelation> SortRelations(this AuthoringWorld world, AuthoringObject obj) =>
		obj.Relations
			.OrderBy(r => r.RelationType, StringComparer.Ordinal)
			.ThenBy(r => r.TargetId);

	public static System.Collections.Generic.IEnumerable<string> SortedOverrides(this AuthoringWorld world, AuthoringObject obj) =>
		obj.OverriddenComponents.OrderBy(n => n, StringComparer.Ordinal);
}
