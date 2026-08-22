// SPDX-License-Identifier: MIT
// StableId.cs —— W1 对象稳定身份（P2.4）
//
// 稳定 Id 是"Rename/Reparent 不破坏引用"门禁的根基：
// 一切引用（父子层级、关系、原型）都指向 StableId，而不是名字或路径。
// Id 由 AuthoringWorld 单调递增分配，随场景持久化保存，往返后保持不变。

using System;
using System.Globalization;

namespace Baize.Authoring;

/// <summary>
/// W1 对象的稳定身份（编辑期/运行时都可用，非 NodePath、非名字）。
/// </summary>
public readonly record struct StableId(ulong Value) : IComparable<StableId>
{
	/// <summary>空 Id：表示"无父级 / 未设置原型 / 待自动分配"。</summary>
	public static readonly StableId None = default;

	public bool IsNone => Value == 0;

	public int CompareTo(StableId other) => Value.CompareTo(other.Value);

	public override string ToString() =>
		Value == 0 ? "none" : "o" + Value.ToString(CultureInfo.InvariantCulture);

	/// <summary>解析 <c>o12</c> 或 <c>12</c> 形式（MCP/日志往返用）。</summary>
	public static bool TryParse(string? text, out StableId id)
	{
		id = None;
		if (string.IsNullOrEmpty(text)) return false;
		if (text[0] == 'o' || text[0] == 'O') text = text[1..];
		if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value) || value == 0)
		{
			return false;
		}
		id = new StableId(value);
		return true;
	}

	/// <summary>解析失败时抛出清晰异常（MCP 工具层用）。</summary>
	public static StableId Parse(string text) =>
		TryParse(text, out var id) ? id : throw new FormatException($"无法解析 StableId：'{text}'（期望 o12 或 12）");
}
