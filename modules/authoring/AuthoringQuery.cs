// SPDX-License-Identifier: MIT
// AuthoringQuery.cs —— W1 结构化查询（P2.4）
//
// "所有 Health<50 的敌人"——人和 AI（MCP）共用同一种查询：
// - 数据形态：AuthoringQuery（可从 JSON 反序列化——MCP 路径）
// - C# 便捷：链式构造 + 强类型谓词
// 条件求值经 Schema 字段读取，数值跨类型提升为 double 比较。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Baize.Authoring;

public enum QueryOperator
{
	Equal,
	NotEqual,
	LessThan,
	LessOrEqual,
	GreaterThan,
	GreaterOrEqual,
}

/// <summary>单条字段条件：组件类型 + 字段名 + 比较符 + 期望值。</summary>
/// <summary>单条字段条件：组件类型 + 字段名 + 比较符 + 期望值。Operator 序列化为字符串名（MCP 友好）。</summary>
public readonly record struct QueryCondition(
	string ComponentType,
	string FieldName,
	[property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
	QueryOperator Operator,
	object Value);

/// <summary>
/// 结构化查询（纯数据，人/AI 共用）：必须组件集 + 字段条件 + 名字包含。
/// </summary>
public sealed class AuthoringQuery
{
	private readonly List<string> _requiredComponents = new();
	private readonly List<QueryCondition> _conditions = new();

	/// <summary>反序列化构造（MCP 从完整 JSON 恢复查询；字段名与属性一致，默认区分大小写）。</summary>
	[JsonConstructor]
	public AuthoringQuery(
		IReadOnlyList<string>? requiredComponents = null,
		IReadOnlyList<QueryCondition>? conditions = null,
		string? nameContains = null)
	{
		if (requiredComponents is not null) _requiredComponents.AddRange(requiredComponents);
		if (conditions is not null) _conditions.AddRange(conditions);
		NameContains = nameContains;
	}
	public IReadOnlyList<string> RequiredComponents => _requiredComponents;
	public IReadOnlyList<QueryCondition> Conditions => _conditions;

	/// <summary>名字包含（Ordinal；null = 不过滤）。名字只是辅助过滤，权威身份仍是 Id。</summary>
	public string? NameContains { get; set; }

	public AuthoringQuery Require(string componentTypeName)
	{
		if (string.IsNullOrWhiteSpace(componentTypeName))
		{
			throw new ArgumentException("组件类型名不能为空", nameof(componentTypeName));
		}
		_requiredComponents.Add(componentTypeName);
		return this;
	}

	public AuthoringQuery Require<TComponent>() =>
		Require(ComponentTypeNameOf<TComponent>());

	public AuthoringQuery Where(string componentTypeName, string fieldName, QueryOperator op, object value)
	{
		if (string.IsNullOrWhiteSpace(fieldName))
		{
			throw new ArgumentException("字段名不能为空", nameof(fieldName));
		}
		_conditions.Add(new QueryCondition(componentTypeName, fieldName, op, value));
		return this;
	}

	public AuthoringQuery Where<TComponent>(string fieldName, QueryOperator op, object value) =>
		Where(ComponentTypeNameOf<TComponent>(), fieldName, op, value);

	public AuthoringQuery Named(string nameSubstring)
	{
		NameContains = nameSubstring;
		return this;
	}

	internal static string ComponentTypeNameOf<TComponent>() =>
		typeof(TComponent).FullName ?? throw new InvalidOperationException($"类型 {typeof(TComponent)} 无 FullName");
}

public static class AuthoringWorldQueryExtensions
{
	/// <summary>执行结构化查询：结果按 StableId 升序（确定性）。</summary>
	public static List<AuthoringObject> Execute(this AuthoringWorld world, AuthoringQuery query)
	{
		if (world is null) throw new ArgumentNullException(nameof(world));
		if (query is null) throw new ArgumentNullException(nameof(query));

		var schemas = query.RequiredComponents.Select(name => world.RequireSchema(name)).ToList();
		IEnumerable<AuthoringObject> candidates = world.Objects.OrderBy(o => o.Id);

		foreach (var schema in schemas)
		{
			candidates = candidates.Where(o => o.Components.ContainsKey(schema.ComponentType));
		}

		if (query.NameContains is not null)
		{
			candidates = candidates.Where(o =>
				o.Name.Contains(query.NameContains, StringComparison.Ordinal));
		}

		var result = new List<AuthoringObject>();
		foreach (var obj in candidates)
		{
			bool match = true;
			foreach (var condition in query.Conditions)
			{
				if (!Matches(world, obj, condition))
				{
					match = false;
					break;
				}
			}
			if (match) result.Add(obj);
		}
		result.Sort((a, b) => a.Id.CompareTo(b.Id));
		return result;
	}

	/// <summary>强类型谓词查询（C# 侧便捷）：拥有 T 组件且谓词为真的对象。</summary>
	public static List<(AuthoringObject Object, TComponent Component)> Execute<TComponent>(
		this AuthoringWorld world, Func<TComponent, bool>? predicate = null)
		where TComponent : struct
	{
		var schema = world.Schema.Get(typeof(TComponent));
		var result = new List<(AuthoringObject, TComponent)>();
		foreach (var obj in world.Objects.OrderBy(o => o.Id))
		{
			if (!obj._components.TryGetValue(schema.ComponentType, out var boxed)) continue;
			var component = (TComponent)boxed;
			if (predicate is null || predicate(component))
			{
				result.Add((obj, component));
			}
		}
		return result;
	}

	private static bool Matches(AuthoringWorld world, AuthoringObject obj, in QueryCondition condition)
	{
		var schema = world.RequireSchema(condition.ComponentType);
		int fieldIndex = FindFieldIndex(schema, condition.FieldName);

		if (!obj._components.TryGetValue(schema.ComponentType, out var boxed)) return false;
		object actual = schema.GetFieldRaw(boxed, fieldIndex);
		return Compare(schema.Fields[fieldIndex].Kind, actual, condition.Operator, condition.Value);
	}

	private static int FindFieldIndex(IComponentSchema schema, string fieldName)
	{
		for (int index = 0; index < schema.Fields.Count; index++)
		{
			if (string.Equals(schema.Fields[index].Name, fieldName, StringComparison.Ordinal))
			{
				return index;
			}
		}
		throw new KeyNotFoundException($"组件 {schema.TypeName} 没有字段 '{fieldName}'");
	}

	private static bool Compare(SchemaFieldKind kind, object actual, QueryOperator op, object expected)
	{
		expected = NormalizeExpectedValue(expected);
		if (kind == SchemaFieldKind.String)
		{
			string actualText = (string)actual;
			string expectedText = expected as string ?? throw new ArgumentException("字符串字段的比较值必须是 string");
			int order = string.CompareOrdinal(actualText, expectedText);
			return op switch
			{
				QueryOperator.Equal => order == 0,
				QueryOperator.NotEqual => order != 0,
				QueryOperator.LessThan => order < 0,
				QueryOperator.LessOrEqual => order <= 0,
				QueryOperator.GreaterThan => order > 0,
				QueryOperator.GreaterOrEqual => order >= 0,
				_ => false,
			};
		}

		// 数值/布尔/枚举：统一转 double 比较（跨 int/float/long 精度在编辑期数值范围内足够）
		double actualValue = ToDouble(kind, actual);
		double expectedValue = expected is IConvertible convertible
			? convertible.ToDouble(System.Globalization.CultureInfo.InvariantCulture)
			: throw new ArgumentException($"条件值类型不支持：{expected?.GetType()}");

		return op switch
		{
			QueryOperator.Equal => actualValue == expectedValue,
			QueryOperator.NotEqual => actualValue != expectedValue,
			QueryOperator.LessThan => actualValue < expectedValue,
			QueryOperator.LessOrEqual => actualValue <= expectedValue,
			QueryOperator.GreaterThan => actualValue > expectedValue,
			QueryOperator.GreaterOrEqual => actualValue >= expectedValue,
			_ => false,
		};
	}

	private static double ToDouble(SchemaFieldKind kind, object raw) => kind switch
	{
		SchemaFieldKind.Int => (int)raw,
		SchemaFieldKind.Long => (long)raw,
		SchemaFieldKind.UInt => (uint)raw,
		SchemaFieldKind.ULong => (ulong)raw,
		SchemaFieldKind.Float => (float)raw,
		SchemaFieldKind.Double => (double)raw,
		SchemaFieldKind.Bool => (bool)raw ? 1 : 0,
		SchemaFieldKind.Enum => Convert.ToDouble(raw),
		SchemaFieldKind.String => throw new InvalidOperationException("字符串字段走专用比较路径"),
		_ => throw new NotSupportedException($"未支持的字段类别：{kind}"),
	};

	/// <summary>
	/// 归一条件值：System.Text.Json 反序列化 object 得到 JsonElement（MCP 路径），
	/// 按 ValueKind 读出标量，使其与强类型路径可比较。
	/// </summary>
	private static object NormalizeExpectedValue(object value) => value switch
	{
		JsonElement json => json.ValueKind switch
		{
			JsonValueKind.String => json.GetString() ?? string.Empty,
			JsonValueKind.Number => json.GetDouble(),
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			_ => throw new ArgumentException($"查询条件值不支持 JSON {json.ValueKind}"),
		},
		_ => value,
	};
}
