// SPDX-License-Identifier: MIT
// AuthoringSchema.cs —— 组件元数据契约与注册表（P2.4）
//
// IComponentSchema 是 W1 看待组件的唯一方式：字段列表 + 按名读写 + 稳定 JSON 序列化。
// 实现由源生成器产出（继承 ComponentSchemaBase，只填 CreateDefault/GetFieldRaw/SetFieldRaw），
// 也可手写——AuthoringSchema.Register 不关心实现来源。
//
// 内存表示 = 装箱 struct；事务/持久化/MCP 的统一中间表示 = JSON（经本 Schema 转换）。

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Baize.Authoring;

/// <summary>组件字段类别（生成器与运行时按同一规则从 FieldType 推导）。</summary>
public enum SchemaFieldKind
{
	Int,
	Long,
	UInt,
	ULong,
	Float,
	Double,
	Bool,
	String,
	Enum,
}

/// <summary>Schema 字段元数据：名字 + 类型 + 声明序号。</summary>
public sealed class SchemaField
{
	public string Name { get; }
	public Type FieldType { get; }
	public int Index { get; }
	public SchemaFieldKind Kind { get; }

	public SchemaField(string name, Type fieldType, int index)
	{
		Name = name ?? throw new ArgumentNullException(nameof(name));
		FieldType = fieldType ?? throw new ArgumentNullException(nameof(fieldType));
		Index = index;
		Kind = Classify(fieldType);
	}

	/// <summary>从字段类型推导 Kind（不支持的类型抛清晰异常——生成器已在编译期拦截）。</summary>
	internal static SchemaFieldKind Classify(Type type)
	{
		if (type.IsEnum) return SchemaFieldKind.Enum;
		if (type == typeof(int)) return SchemaFieldKind.Int;
		if (type == typeof(long)) return SchemaFieldKind.Long;
		if (type == typeof(uint)) return SchemaFieldKind.UInt;
		if (type == typeof(ulong)) return SchemaFieldKind.ULong;
		if (type == typeof(float)) return SchemaFieldKind.Float;
		if (type == typeof(double)) return SchemaFieldKind.Double;
		if (type == typeof(bool)) return SchemaFieldKind.Bool;
		if (type == typeof(string)) return SchemaFieldKind.String;
		throw new NotSupportedException($"不支持的字段类型：{type}（支持 int/uint/long/ulong/float/double/bool/string/enum）");
	}
}

/// <summary>组件 Schema 契约：W1 对组件数据的全部操作都经过它。</summary>
public interface IComponentSchema
{
	/// <summary>组件 CLR 类型。</summary>
	Type ComponentType { get; }

	/// <summary>组件类型全名（持久化键 / MCP 引用名，如 "Shooter.Gameplay.Health"）。</summary>
	string TypeName { get; }

	/// <summary>字段列表（按声明顺序）。</summary>
	IReadOnlyList<SchemaField> Fields { get; }

	/// <summary>创建默认值（装箱 struct）。</summary>
	object CreateDefault();

	/// <summary>读第 <paramref name="index"/> 个字段的装箱值。</summary>
	object GetFieldRaw(object component, int index);

	/// <summary>写第 <paramref name="index"/> 个字段（<paramref name="value"/> 必须已是字段类型）。</summary>
	void SetFieldRaw(ref object component, int index, object value);

	/// <summary>值拷贝（struct 复制；string 字段不可变，浅拷贝安全）。</summary>
	object Clone(object component);

	/// <summary>逐字段值相等（diff 与 hash 的基础）。</summary>
	bool ValueEquals(object a, object b);

	/// <summary>写稳定 JSON：字段按声明顺序、枚举写字符串名、缩进由外层 writer 控制。</summary>
	void WriteJson(Utf8JsonWriter writer, object component);

	/// <summary>从 JSON 读组件（缺字段保持默认，未知字段忽略——向前兼容）。</summary>
	object ReadJson(JsonElement element);

	/// <summary>组件值 → JsonElement（事务 op 与快照的统一中间表示）。</summary>
	JsonElement ToJson(object component);
}

/// <summary>
/// Schema 基类：通用逻辑（Clone/ValueEquals/JSON 往返）在此实现一次，
/// 生成代码只需提供 CreateDefault + 强类型 Get/Set switch。
/// </summary>
public abstract class ComponentSchemaBase : IComponentSchema
{
	private readonly SchemaField[] _fields;

	protected ComponentSchemaBase(Type componentType, SchemaField[] fields)
	{
		ComponentType = componentType ?? throw new ArgumentNullException(nameof(componentType));
		_fields = fields ?? throw new ArgumentNullException(nameof(fields));
	}

	public Type ComponentType { get; }

	public string TypeName => ComponentType.FullName!;

	public IReadOnlyList<SchemaField> Fields => _fields;

	public abstract object CreateDefault();

	public abstract object GetFieldRaw(object component, int index);

	public abstract void SetFieldRaw(ref object component, int index, object value);

	public object Clone(object component)
	{
		object copy = CreateDefault();
		foreach (var field in _fields)
		{
			SetFieldRaw(ref copy, field.Index, GetFieldRaw(component, field.Index));
		}
		return copy;
	}

	public bool ValueEquals(object a, object b)
	{
		if (ReferenceEquals(a, b)) return true;
		if (a is null || b is null) return false;
		foreach (var field in _fields)
		{
			if (!Equals(GetFieldRaw(a, field.Index), GetFieldRaw(b, field.Index)))
			{
				return false;
			}
		}
		return true;
	}

	public void WriteJson(Utf8JsonWriter writer, object component)
	{
		writer.WriteStartObject();
		foreach (var field in _fields)
		{
			object value = GetFieldRaw(component, field.Index);
			switch (field.Kind)
			{
				case SchemaFieldKind.Int: writer.WriteNumber(field.Name, (int)value); break;
				case SchemaFieldKind.Long: writer.WriteNumber(field.Name, (long)value); break;
				case SchemaFieldKind.UInt: writer.WriteNumber(field.Name, (uint)value); break;
				case SchemaFieldKind.ULong: writer.WriteNumber(field.Name, (ulong)value); break;
				case SchemaFieldKind.Float: writer.WriteNumber(field.Name, (float)value); break;
				case SchemaFieldKind.Double: writer.WriteNumber(field.Name, (double)value); break;
				case SchemaFieldKind.Bool: writer.WriteBoolean(field.Name, (bool)value); break;
				case SchemaFieldKind.String: writer.WriteString(field.Name, (value as string) ?? string.Empty); break;
				case SchemaFieldKind.Enum:
					writer.WriteString(field.Name, ((Enum)value).ToString());   // 枚举写名字：Git diff 友好
					break;
				default:
					throw new NotSupportedException($"未支持的字段类别：{field.Kind}");
			}
		}
		writer.WriteEndObject();
	}

	public object ReadJson(JsonElement element)
	{
		object component = CreateDefault();
		if (element.ValueKind != JsonValueKind.Object)
		{
			// 非对象形态（数字/字符串/null/数组）是数据错误，静默重置为默认值会无提示丢数据
			throw new FormatException($"组件 JSON 必须是对象形态，实际为 {element.ValueKind}：{element.GetRawText()}");
		}
		foreach (var field in _fields)
		{
			if (!element.TryGetProperty(field.Name, out var json)) continue;
			SetFieldRaw(ref component, field.Index, ReadValue(field, json));
		}
		return component;
	}

	public JsonElement ToJson(object component)
	{
		var buffer = new System.Buffers.ArrayBufferWriter<byte>();
		using var writer = new Utf8JsonWriter(buffer);
		WriteJson(writer, component);
		writer.Flush();
		using var document = JsonDocument.Parse(buffer.WrittenSpan.ToArray());
		return document.RootElement.Clone();
	}

	private static object ReadValue(SchemaField field, JsonElement json)
	{
		switch (field.Kind)
		{
			case SchemaFieldKind.Int: return json.GetInt32();
			case SchemaFieldKind.Long: return json.GetInt64();
			case SchemaFieldKind.UInt: return json.GetUInt32();
			case SchemaFieldKind.ULong: return json.GetUInt64();
			case SchemaFieldKind.Float: return json.GetSingle();
			case SchemaFieldKind.Double: return json.GetDouble();
			case SchemaFieldKind.Bool: return json.GetBoolean();
			case SchemaFieldKind.String: return json.GetString() ?? string.Empty;
			case SchemaFieldKind.Enum: return ReadEnum(field, json);
			default:
				throw new NotSupportedException($"未支持的字段类别：{field.Kind}");
		}
	}

	private static object ReadEnum(SchemaField field, JsonElement json)
	{
		if (json.ValueKind == JsonValueKind.Number)
		{
			// 数字路径按底层类型读取：ulong 底层的枚举可能超出 Int64 范围
			return Enum.ToObject(field.FieldType,
				Enum.GetUnderlyingType(field.FieldType) == typeof(ulong) ? json.GetUInt64() : json.GetInt64());
		}
		string? name = json.GetString();
		if (name is not null && TryParseEnumName(field.FieldType, name, out var parsed))
		{
			return parsed!;   // TryParse 成功时 parsed 非空
		}
		throw new FormatException($"枚举字段 '{field.Name}' 无法解析值 '{json.GetRawText()}'（类型 {field.FieldType}）");
	}

	private static bool TryParseEnumName(Type enumType, string name, out object? value)
	{
		try
		{
			value = Enum.Parse(enumType, name, ignoreCase: false);
			return true;
		}
		catch (ArgumentException)
		{
			value = null;
			return false;
		}
	}
}

/// <summary>
/// Authoring Schema 注册表：组件类型 ↔ 元数据。游戏程序集的生成产物
/// （AuthoringSchemaRegistration.RegisterAll）是唯一的批量注册入口。
/// </summary>
public sealed class AuthoringSchema
{
	private readonly Dictionary<Type, IComponentSchema> _byType = new();
	private readonly Dictionary<string, IComponentSchema> _byName = new(StringComparer.Ordinal);
	private readonly List<IComponentSchema> _all = new();

	public IReadOnlyCollection<IComponentSchema> All => _all;

	/// <summary>注册一个组件 Schema；重复注册同一类型抛异常（声明冲突要暴露，不要静默覆盖）。</summary>
	public void Register(IComponentSchema schema)
	{
		if (_byType.ContainsKey(schema.ComponentType))
		{
			throw new InvalidOperationException($"组件 Schema 重复注册：{schema.ComponentType}");
		}
		_byType[schema.ComponentType] = schema;
		_byName[schema.TypeName] = schema;
		_all.Add(schema);
	}

	public bool IsRegistered(Type componentType) => _byType.ContainsKey(componentType);

	public IComponentSchema Get(Type componentType) =>
		TryGet(componentType, out var schema)
			? schema
			: throw new KeyNotFoundException(
				$"组件类型未注册进 AuthoringSchema：{componentType}（确认 [Component] 标注且调用了 RegisterAll）");

	public IComponentSchema GetByName(string typeName) =>
		TryGetByName(typeName, out var schema)
			? schema
			: throw new KeyNotFoundException($"组件类型名未注册进 AuthoringSchema：'{typeName}'");

	public bool TryGet(Type componentType, out IComponentSchema schema) =>
		_byType.TryGetValue(componentType, out schema!);

	public bool TryGetByName(string typeName, out IComponentSchema schema) =>
		_byName.TryGetValue(typeName, out schema!);
}
