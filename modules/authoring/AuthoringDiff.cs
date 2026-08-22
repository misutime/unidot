// SPDX-License-Identifier: MIT
// AuthoringDiff.cs —— 事务执行结果的可观测差异（P2.4）
//
// diff 由事务执行器统一产出：UI 面板与 MCP 工具看到的永远是同一种 diff（门禁）。
// record 语义——两条路径产生的 diff 可直接相等断言。

using System;
using System.Collections.Generic;

namespace Baize.Authoring;

public enum AuthoringDiffKind
{
	ObjectCreated,
	ObjectDeleted,
	Renamed,
	Reparented,
	ComponentAdded,
	ComponentRemoved,
	ComponentChanged,
	RelationAdded,
	RelationRemoved,
	PrototypeChanged,
}

/// <summary>单条差异：谁、发生了什么、可读描述。</summary>
public sealed record AuthoringDiffEntry(
	AuthoringDiffKind Kind,
	StableId ObjectId,
	string? ComponentType,
	string Detail);

/// <summary>
/// 一次事务（或一次 Undo/Redo）产生的差异集合。
/// 手写值相等：逐条比较 Entries（record 对集合属性默认是引用相等，
/// 而"UI 与 MCP 产生相同 diff"门禁需要的是内容相等）。
/// </summary>
public sealed record AuthoringDiff
{
	public static readonly AuthoringDiff Empty = new(Array.Empty<AuthoringDiffEntry>());

	public IReadOnlyList<AuthoringDiffEntry> Entries { get; }

	public AuthoringDiff(IReadOnlyList<AuthoringDiffEntry> entries)
	{
		Entries = entries;
	}

	public bool Equals(AuthoringDiff? other)
	{
		if (other is null || Entries.Count != other.Entries.Count) return false;
		for (int index = 0; index < Entries.Count; index++)
		{
			if (!Entries[index].Equals(other.Entries[index])) return false;
		}
		return true;
	}

	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (var entry in Entries) hash.Add(entry);
		return hash.ToHashCode();
	}
	public override string ToString() =>
		Entries.Count == 0 ? "（无差异）" : string.Join("; ", Entries);
}
