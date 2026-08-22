// SPDX-License-Identifier: MIT
// TransactionExecution.cs —— AuthoringWorld 的事务执行引擎（P2.4）
//
// Apply/Undo/Redo 的全部机制：
// - 执行：逐 op 改状态 + 记录操作级逆数据（UndoEntry）+ 产出 diff；任一 op 失败则
//   逆放已执行部分（原子性），世界回到 Apply 前状态。
// - Undo：逆序恢复 UndoEntry（记录的是绝对旧值，与中间历史无关，可反复 undo/redo）。
// - Redo：重放规范化后的 ops（自动分配的 Id 已落实为显式 Id，重放完全确定）。
//
// 门禁对应：UI 与 MCP 构造同一事务 → 本引擎产出同一 diff；Undo/Redo 后 ArtifactHash 恢复。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Baize.Authoring;

/// <summary>事务执行失败（含 op 序号与原因）；世界保持 Apply 前状态。</summary>
public sealed class AuthoringTransactionException : Exception
{
	public AuthoringTransactionException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed partial class AuthoringWorld
{
	/// <summary>应用一个事务：全部生效或全部不生效。返回统一 diff。</summary>
	public AuthoringDiff Apply(AuthoringTransaction transaction)
	{
		if (transaction is null) throw new ArgumentNullException(nameof(transaction));
		if (transaction.Count == 0) return AuthoringDiff.Empty;

		// 入口规范化：组件 JSON 经 Schema 读入再输出（语义等价的 MCP 输入收敛为同一形态），
		// 同时让历史栈持有独立 JsonElement（与调用方 JsonDocument 生命周期解耦）。
		AuthoringTransaction canonical;
		try
		{
			canonical = transaction.Canonicalize(Schema);
		}
		catch (Exception ex)
		{
			throw new AuthoringTransactionException($"事务规范化失败（世界未改变）：{ex.Message}", ex);
		}

		_version++;
		var applied = new AppliedTransaction();
		applied.NextIdBefore = _nextId;   // 回滚/Undo 需恢复计数器，保证 ArtifactHash 完全还原
		var diffs = new List<AuthoringDiffEntry>();
		var touched = new HashSet<StableId>();
		try
		{
			for (int index = 0; index < canonical.Ops.Count; index++)
			{
				AuthoringOp op = canonical.Ops[index];
				try
				{
					applied.NormalizedOps.Add(ExecuteOp(op, applied.UndoEntries, diffs, touched, index));
				}
				catch (Exception failure)
				{
					throw new AuthoringTransactionException(
						$"事务在第 {index + 1}/{canonical.Count} 个操作失败：{failure.Message}", failure);
				}
			}
		}
		catch (Exception ex)
		{
			Rollback(applied.UndoEntries);
			_nextId = applied.NextIdBefore;   // 计数器一并还原（自动分配的 Id 也回退）
			foreach (StableId id in touched) ForgetObject(id);   // 回滚版本痕迹
			_version--;
			throw new AuthoringTransactionException($"事务已回滚（世界未改变）。{ex.Message}", ex);
		}

		foreach (StableId id in touched) TouchObject(id);
		applied.NextIdAfter = _nextId;   // 提交时记录水位：Undo 仅在未被外部推进时回退
		var diff = new AuthoringDiff(diffs);
		applied.Diff = diff;
		_undoStack.Add(applied);
		_redoStack.Clear();   // 新分支：redo 历史作废
		return diff;
	}

	/// <summary>撤销最近一次事务。返回逆向 diff。</summary>
	public AuthoringDiff Undo()
	{
		if (_undoStack.Count == 0) throw new InvalidOperationException("没有可撤销的事务");

		_version++;
		var applied = _undoStack[^1];
		_undoStack.RemoveAt(_undoStack.Count - 1);

		var diffs = new List<AuthoringDiffEntry>();
		var touched = new HashSet<StableId>();
		try
		{
			for (int index = applied.UndoEntries.Count - 1; index >= 0; index--)
			{
				applied.UndoEntries[index].Restore(this, touched);
			}
			CollectUndoDescriptions(applied, diffs);
		}
		catch (Exception ex)
		{
			throw new AuthoringTransactionException($"撤销事务失败（世界可能已不一致）：{ex.Message}", ex);
		}

		foreach (StableId id in touched) TouchObject(id);
		// 计数器回退仅在未被外部推进时执行：AllocateIds 是公开 API，事务外预留的空洞
		// 不能被 Undo 回收（否则会再次发出同一 Id 造成重复）
		if (_nextId == applied.NextIdAfter)
		{
			_nextId = applied.NextIdBefore;
		}
		_redoStack.Add(applied);
		return new AuthoringDiff(diffs);
	}

	/// <summary>重做最近一次被撤销的事务（重放规范化 ops——完全确定）。</summary>
	public AuthoringDiff Redo()
	{
		if (_redoStack.Count == 0) throw new InvalidOperationException("没有可重做的事务");

		_version++;
		var applied = _redoStack[^1];
		_redoStack.RemoveAt(_redoStack.Count - 1);

		var scratchUndos = new List<UndoEntry>();   // 重放产生的逆数据不需要（原 entries 依然有效）
		var diffs = new List<AuthoringDiffEntry>();
		var touched = new HashSet<StableId>();
		try
		{
			for (int index = 0; index < applied.NormalizedOps.Count; index++)
			{
				ExecuteOp(applied.NormalizedOps[index], scratchUndos, diffs, touched, index);
			}
		}
		catch (Exception ex)
		{
			Rollback(scratchUndos);
			_version--;
			_redoStack.Add(applied);   // 归还 redo 栈
			throw new AuthoringTransactionException($"重做事务失败：{ex.Message}", ex);
		}

		foreach (StableId id in touched) TouchObject(id);
		_undoStack.Add(applied);
		return new AuthoringDiff(diffs);
	}

	private void Rollback(List<UndoEntry> undoEntries)
	{
		for (int index = undoEntries.Count - 1; index >= 0; index--)
		{
			undoEntries[index].Restore(this, new HashSet<StableId>());
		}
	}

	private void CollectUndoDescriptions(AppliedTransaction applied, List<AuthoringDiffEntry> diffs)
	{
		// 逆序描述：与恢复顺序一致，读起来是"从新到旧"的还原过程
		for (int index = applied.UndoEntries.Count - 1; index >= 0; index--)
		{
			diffs.AddRange(applied.UndoEntries[index].Describe(this));
		}
	}

	private AuthoringOp ExecuteOp(
		AuthoringOp op,
		List<UndoEntry> undos,
		List<AuthoringDiffEntry> diffs,
		HashSet<StableId> touched,
		int opIndex)
	{
		switch (op)
		{
			case CreateObjectOp create:
			{
				if (string.IsNullOrWhiteSpace(create.Name))
				{
					throw new ArgumentException("对象名不能为空");
				}
				StableId id = create.Id;
				bool autoAllocated = id.IsNone;
				if (autoAllocated)
				{
					id = AllocateId();
				}
				else
				{
					if (Exists(id))
					{
						throw new InvalidOperationException($"对象 {id} 已存在，不能重复创建");
					}
					// 显式大 Id 也推进计数器，避免未来自动分配撞上已占用 Id
					if (id.Value >= _nextId)
					{
						_nextId = checked(id.Value + 1);
					}
				}
				if (!create.ParentId.IsNone && !Exists(create.ParentId))
				{
					throw new KeyNotFoundException($"父对象不存在：{create.ParentId}");
				}

				undos.Add(new DeleteCreatedEntry(id));
				var obj = new AuthoringObject(id, create.Name) { ParentId = create.ParentId };
				_objects[id] = obj;
				AddChildIndex(create.ParentId, id);
				touched.Add(id);
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.ObjectCreated, id, null,
					$"创建对象 '{create.Name}'（{id}{(autoAllocated ? "，自动分配 Id" : "")}{(create.ParentId.IsNone ? "" : $"，父级 {create.ParentId}")}）"));
				return autoAllocated ? create with { Id = id } : op;   // 规范化：Redo 用显式 Id
			}

			case DeleteObjectOp delete:
			{
				var root = Require(delete.Id);
				var subtree = new List<AuthoringObject>();
				CollectSubtree(root, subtree);

				// 入引用预检：子树外对象的原型/关系指向将被删除的对象 → 拒绝（悬空引用会破坏 Baker 与关系不变量）
				var doomed = new HashSet<StableId>(subtree.Select(node => node.Id));
				foreach (var other in _objects.Values)
				{
					if (doomed.Contains(other.Id)) continue;
					if (other.PrototypeId is { } protoRef && doomed.Contains(protoRef))
					{
						throw new InvalidOperationException(
							$"无法删除 {delete.Id}：对象 {other.Id}（{other.Name}）的原型指向它，请先解除引用");
					}
					foreach (var relation in other._relations.Where(relation => doomed.Contains(relation.TargetId)))
					{
						throw new InvalidOperationException(
							$"无法删除 {delete.Id}：对象 {other.Id}（{other.Name}）的关系 [{relation.RelationType}] 指向它，请先解除引用");
					}
				}

				var snapshots = new List<ObjectSnapshot>(subtree.Count);
				foreach (var node in subtree)
				{
					snapshots.Add(CaptureSnapshot(node));
				}
				undos.Add(new RestoreSnapshotsEntry(snapshots));

				foreach (var node in subtree)
				{
					RemoveChildIndex(node.ParentId, node.Id);
					_objects.Remove(node.Id);
					ForgetObject(node.Id);
					touched.Add(node.Id);
				}
				string cascade = subtree.Count > 1 ? $"（级联删除 {subtree.Count - 1} 个子孙）" : "";
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.ObjectDeleted, delete.Id, null,
					$"删除对象 '{root.Name}'（{delete.Id}）{cascade}"));
				return op;
			}

			case RenameObjectOp rename:
			{
				var obj = Require(rename.Id);
				if (string.IsNullOrWhiteSpace(rename.NewName))
				{
					throw new ArgumentException("对象名不能为空");
				}
				string oldName = obj._name;
				undos.Add(new RenameEntry(rename.Id, oldName));
				obj._name = rename.NewName;
				touched.Add(obj.Id);
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.Renamed, rename.Id, null,
					$"'{oldName}' → '{rename.NewName}'"));
				return op;
			}

			case ReparentObjectOp reparent:
			{
				var obj = Require(reparent.Id);
				if (!reparent.NewParentId.IsNone && !Exists(reparent.NewParentId))
				{
					throw new KeyNotFoundException($"目标父对象不存在：{reparent.NewParentId}");
				}
				if (IsAncestorOrSelf(obj.Id, reparent.NewParentId))
				{
					throw new InvalidOperationException(
						$"不能把 {obj.Id} 移到自己或自己的子孙（{reparent.NewParentId}）之下");
				}
				StableId oldParent = obj.ParentId;
				undos.Add(new ReparentEntry(obj.Id, oldParent));
				MoveChild(obj, reparent.NewParentId);
				touched.Add(obj.Id);
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.Reparented, obj.Id, null,
					$"父级 {FormatParent(oldParent)} → {FormatParent(reparent.NewParentId)}"));
				return op;
			}

			case AddComponentOp add:
			{
				var obj = Require(add.Id);
				var schema = RequireSchema(add.ComponentType);
				if (obj._components.ContainsKey(schema.ComponentType))
				{
					throw new InvalidOperationException(
						$"对象 {add.Id} 已有组件 {add.ComponentType}（改值请用 SetComponentOp）");
				}
				object value = schema.ReadJson(add.Value);
				undos.Add(ComponentValueEntry.RemovedState(add.ComponentType, obj));
				obj._components[schema.ComponentType] = value;
				MarkLocalOverride(obj, add.ComponentType);
				touched.Add(obj.Id);
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.ComponentAdded, obj.Id, add.ComponentType,
					$"{obj.Name} 新增组件 {add.ComponentType} = {add.Value.GetRawText()}"));
				return op;
			}

			case SetComponentOp setComponent:
			{
				var obj = Require(setComponent.Id);
				var schema = RequireSchema(setComponent.ComponentType);
				bool existed = obj._components.TryGetValue(schema.ComponentType, out var oldValue);
				object value = schema.ReadJson(setComponent.Value);
				undos.Add(existed
					? ComponentValueEntry.PreviousValue(schema, oldValue!, obj)
					: ComponentValueEntry.RemovedState(setComponent.ComponentType, obj));
				obj._components[schema.ComponentType] = value;
				MarkLocalOverride(obj, setComponent.ComponentType);
				touched.Add(obj.Id);
				diffs.Add(new AuthoringDiffEntry(
					existed ? AuthoringDiffKind.ComponentChanged : AuthoringDiffKind.ComponentAdded,
					obj.Id, setComponent.ComponentType,
					$"{obj.Name} 组件 {setComponent.ComponentType} = {setComponent.Value.GetRawText()}" +
					(existed ? $"（原 {schema.ToJson(oldValue!).GetRawText()}）" : "")));
				return op;
			}

			case RemoveComponentOp remove:
			{
			var obj = Require(remove.Id);
			var schema = RequireSchema(remove.ComponentType);
			if (!obj._components.TryGetValue(schema.ComponentType, out var oldValue))
			{
				// 本地没有该组件：若它来自原型继承，"显式删除"是合法的 Prefab 覆盖（记 override，本地保持无）
				bool inherited = obj.PrototypeId is not null
					&& ResolveEffectiveComponents(obj).ContainsKey(schema.ComponentType);
				if (!inherited)
				{
					throw new KeyNotFoundException($"对象 {remove.Id} 没有组件 {remove.ComponentType}");
				}
				undos.Add(ComponentValueEntry.RemovedState(remove.ComponentType, obj));
				obj._overrides.Add(remove.ComponentType);
				touched.Add(obj.Id);
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.ComponentRemoved, obj.Id, remove.ComponentType,
					$"{obj.Name} 显式删除继承组件 {remove.ComponentType}（相对原型 {obj.PrototypeId}）"));
				return op;
			}
			undos.Add(ComponentValueEntry.PreviousValue(schema, oldValue, obj));
			obj._components.Remove(schema.ComponentType);
			MarkLocalOverride(obj, remove.ComponentType);   // 显式删除也是 override
			touched.Add(obj.Id);
			diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.ComponentRemoved, obj.Id, remove.ComponentType,
				$"{obj.Name} 移除组件 {remove.ComponentType}（原 {schema.ToJson(oldValue).GetRawText()}）"));
				return op;
			}

			case AddRelationOp addRelation:
			{
				var obj = Require(addRelation.Id);
				if (string.IsNullOrWhiteSpace(addRelation.RelationType))
				{
					throw new ArgumentException("关系类型不能为空");
				}
				if (!Exists(addRelation.TargetId))
				{
					throw new KeyNotFoundException($"关系目标不存在：{addRelation.TargetId}");
				}
				var relation = new AuthoringRelation(addRelation.RelationType, addRelation.TargetId);
				if (obj._relations.Contains(relation))
				{
					throw new InvalidOperationException(
						$"对象 {addRelation.Id} 已有关系 {relation.RelationType} → {relation.TargetId}");
				}
				undos.Add(new RelationEntry(obj.Id, relation, added: true));
				obj._relations.Add(relation);
				touched.Add(obj.Id);
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.RelationAdded, obj.Id, null,
					$"{obj.Name} —[{relation.RelationType}]→ {relation.TargetId}"));
				return op;
			}

			case RemoveRelationOp removeRelation:
			{
				var obj = Require(removeRelation.Id);
				var relation = new AuthoringRelation(removeRelation.RelationType, removeRelation.TargetId);
				if (!obj._relations.Remove(relation))
				{
					throw new KeyNotFoundException(
						$"对象 {removeRelation.Id} 没有关系 {relation.RelationType} → {relation.TargetId}");
				}
				undos.Add(new RelationEntry(obj.Id, relation, added: false));
				touched.Add(obj.Id);
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.RelationRemoved, obj.Id, null,
					$"{obj.Name} 移除关系 [{relation.RelationType}]→ {relation.TargetId}"));
				return op;
			}

			case SetPrototypeOp setPrototype:
			{
				var obj = Require(setPrototype.Id);
				StableId? newPrototype = setPrototype.PrototypeId.IsNone ? null : setPrototype.PrototypeId;
				if (newPrototype is { } prototypeId)
				{
					if (!Exists(prototypeId))
					{
						throw new KeyNotFoundException($"原型对象不存在：{prototypeId}");
					}
					if (PrototypeChainReaches(prototypeId, obj.Id))
					{
						throw new InvalidOperationException(
							$"不能把 {prototypeId} 设为 {obj.Id} 的原型：沿原型链会回到自身（形成环）");
					}
				}
				undos.Add(new PrototypeEntry(obj.Id, obj.PrototypeId, obj._overrides));
				obj.PrototypeId = newPrototype;
				if (newPrototype is null)
				{
					// 清除原型 = 退化为普通对象：override 记录失去参照系，事务化清空（Undo 可还原）
					obj._overrides.Clear();
				}
				touched.Add(obj.Id);
				diffs.Add(new AuthoringDiffEntry(AuthoringDiffKind.PrototypeChanged, obj.Id, null,
					newPrototype is null
						? $"{obj.Name} 清除原型"
						: $"{obj.Name} 原型 = {newPrototype}"));
				return op;
			}

			default:
				throw new NotSupportedException($"未知的事务操作类型：{op?.GetType().FullName ?? "null"}");
		}
	}

	// —— 内部辅助 ——

	/// <summary>沿 PrototypeId 链从 start 向上追溯是否到达 target（SetPrototype 的环预检）。</summary>
	private bool PrototypeChainReaches(StableId start, StableId target)
	{
		var guard = new HashSet<StableId>();
		StableId current = start;
		while (!current.IsNone && guard.Add(current))
		{
			if (current == target) return true;
			current = Find(current)?.PrototypeId ?? StableId.None;
		}
		return false;
	}

	internal IComponentSchema RequireSchema(string typeName) =>
		Schema.TryGetByName(typeName, out var schema)
			? schema
			: throw new KeyNotFoundException(
				$"组件类型 '{typeName}' 未注册进 AuthoringSchema（确认 [Component] 标注且调用了 RegisterAll）");

	private void MarkLocalOverride(AuthoringObject obj, string componentTypeName)
	{
		// override 只对"实例"有意义：有原型时本地组件值即覆盖
		if (obj.PrototypeId is not null)
		{
			obj._overrides.Add(componentTypeName);
		}
	}

	private void CollectSubtree(AuthoringObject root, List<AuthoringObject> output)
	{
		output.Add(root);
		if (_children.TryGetValue(root.Id, out var childIds))
		{
			foreach (StableId childId in childIds.ToArray())
			{
				CollectSubtree(Require(childId), output);
			}
		}
	}

	private ObjectSnapshot CaptureSnapshot(AuthoringObject obj)
	{
		var components = new List<KeyValuePair<string, JsonElement>>(obj._components.Count);
		foreach (var pair in this.SortedComponents(obj))
		{
			components.Add(new(Schema.Get(pair.Key).TypeName, Schema.Get(pair.Key).ToJson(pair.Value)));
		}

		var relations = new List<AuthoringRelation>(obj._relations);
		var overrides = new List<string>(obj._overrides);
		overrides.Sort(StringComparer.Ordinal);

		return new ObjectSnapshot(obj.Id, obj.Name, obj.ParentId, obj.PrototypeId, components, relations, overrides);
	}

	private void RestoreSnapshot(ObjectSnapshot snapshot, HashSet<StableId> touched)
	{
		if (_objects.ContainsKey(snapshot.Id))
		{
			throw new InvalidOperationException($"恢复快照冲突：对象 {snapshot.Id} 已存在");
		}
		var obj = new AuthoringObject(snapshot.Id, snapshot.Name)
		{
			ParentId = snapshot.ParentId,
			PrototypeId = snapshot.PrototypeId,
		};
		foreach (var pair in snapshot.Components)
		{
			var schema = RequireSchema(pair.Key);
			obj._components[schema.ComponentType] = schema.ReadJson(pair.Value);
		}
		obj._relations.AddRange(snapshot.Relations);
		obj._overrides.UnionWith(snapshot.Overrides);

		_objects[obj.Id] = obj;
		AddChildIndex(obj.ParentId, obj.Id);
		touched.Add(obj.Id);
	}

	private void MoveChild(AuthoringObject obj, StableId newParent)
	{
		RemoveChildIndex(obj.ParentId, obj.Id);
		obj.ParentId = newParent;
		AddChildIndex(newParent, obj.Id);
	}

	private void AddChildIndex(StableId parent, StableId child)
	{
		if (!_children.TryGetValue(parent, out var list))
		{
			list = new List<StableId>();
			_children[parent] = list;
		}
		list.Add(child);
	}

	private void RemoveChildIndex(StableId parent, StableId child)
	{
		if (_children.TryGetValue(parent, out var list))
		{
			list.Remove(child);
			if (list.Count == 0 && !parent.IsNone) _children.Remove(parent);
		}
	}

	private static string FormatParent(StableId parentId) => parentId.IsNone ? "根" : parentId.ToString();

	// —— 快照（Delete 级联恢复的最小完整单元） ——

	internal sealed record ObjectSnapshot(
		StableId Id,
		string Name,
		StableId ParentId,
		StableId? PrototypeId,
		IReadOnlyList<KeyValuePair<string, JsonElement>> Components,
		IReadOnlyList<AuthoringRelation> Relations,
		IReadOnlyList<string> Overrides);

	// —— Undo 条目：记录绝对旧值，Restore 与中间历史无关 ——

	internal abstract class UndoEntry
	{
		public abstract void Restore(AuthoringWorld world, HashSet<StableId> touched);

		public abstract IEnumerable<AuthoringDiffEntry> Describe(AuthoringWorld world);
	}

	/// <summary>逆"创建对象"：删除它（栈式不变量下此刻无子孙）。</summary>
	internal sealed class DeleteCreatedEntry(StableId id) : UndoEntry
	{
		public override void Restore(AuthoringWorld world, HashSet<StableId> touched)
		{
			var obj = world.Require(id);
			world.RemoveChildIndex(obj.ParentId, id);
			world._objects.Remove(id);
			world.ForgetObject(id);
			touched.Add(id);
		}

		public override IEnumerable<AuthoringDiffEntry> Describe(AuthoringWorld world)
		{
			yield return new AuthoringDiffEntry(AuthoringDiffKind.ObjectDeleted, id, null, $"撤销创建：移除 {id}");
		}
	}

	/// <summary>逆"删除对象（级联）"：按快照整树恢复。</summary>
	internal sealed class RestoreSnapshotsEntry(List<ObjectSnapshot> snapshots) : UndoEntry
	{
		public override void Restore(AuthoringWorld world, HashSet<StableId> touched)
		{
			foreach (var snapshot in snapshots)
			{
				world.RestoreSnapshot(snapshot, touched);
			}
		}

		public override IEnumerable<AuthoringDiffEntry> Describe(AuthoringWorld world)
		{
			yield return new AuthoringDiffEntry(AuthoringDiffKind.ObjectCreated, snapshots[0].Id, null,
				snapshots.Count > 1
					? $"撤销删除：恢复 '{snapshots[0].Name}' 及 {snapshots.Count - 1} 个子孙"
					: $"撤销删除：恢复 '{snapshots[0].Name}'");
		}
	}

	internal sealed class RenameEntry(StableId id, string oldName) : UndoEntry
	{
		public override void Restore(AuthoringWorld world, HashSet<StableId> touched)
		{
			world.Require(id)._name = oldName;
			touched.Add(id);
		}

		public override IEnumerable<AuthoringDiffEntry> Describe(AuthoringWorld world) =>
			[new AuthoringDiffEntry(AuthoringDiffKind.Renamed, id, null, $"撤销改名：恢复为 '{oldName}'")];
	}

	internal sealed class ReparentEntry(StableId id, StableId oldParent) : UndoEntry
	{
		public override void Restore(AuthoringWorld world, HashSet<StableId> touched)
		{
			world.MoveChild(world.Require(id), oldParent);
			touched.Add(id);
		}

		public override IEnumerable<AuthoringDiffEntry> Describe(AuthoringWorld world) =>
			[new AuthoringDiffEntry(AuthoringDiffKind.Reparented, id, null,
				$"撤销移动：父级恢复为 {FormatParent(oldParent)}")];
	}

	/// <summary>组件值的旧状态：存在（带 JSON 旧值）或不存在；连同当时的 override 标记一起还原。</summary>
	internal sealed class ComponentValueEntry(
		StableId id,
		string typeName,
		JsonElement? oldValue,
		bool existed,
		bool hadOverrideMark) : UndoEntry
	{
		public static ComponentValueEntry PreviousValue(IComponentSchema schema, object value, AuthoringObject obj) =>
			new(obj.Id, schema.TypeName, schema.ToJson(value), existed: true, obj._overrides.Contains(schema.TypeName));

		public static ComponentValueEntry RemovedState(string typeName, AuthoringObject obj) =>
			new(obj.Id, typeName, null, existed: false, obj._overrides.Contains(typeName));

		public override void Restore(AuthoringWorld world, HashSet<StableId> touched)
		{
			var obj = world.Require(id);
			var type = world.RequireSchema(typeName).ComponentType;
			if (existed)
			{
				obj._components[type] = world.RequireSchema(typeName).ReadJson(oldValue!.Value);
			}
			else
			{
				obj._components.Remove(type);
			}

			if (hadOverrideMark) obj._overrides.Add(typeName);
			else obj._overrides.Remove(typeName);
			touched.Add(id);
		}

		public override IEnumerable<AuthoringDiffEntry> Describe(AuthoringWorld world) =>
			[new AuthoringDiffEntry(
				existed ? AuthoringDiffKind.ComponentChanged : AuthoringDiffKind.ComponentRemoved,
				id, typeName,
				existed ? $"撤销修改：{typeName} 恢复为 {oldValue!.Value.GetRawText()}" : $"撤销添加：移除 {typeName}")];
	}

	internal sealed class RelationEntry(StableId id, AuthoringRelation relation, bool added) : UndoEntry
	{
		public override void Restore(AuthoringWorld world, HashSet<StableId> touched)
		{
			var obj = world.Require(id);
			if (added)
			{
				obj._relations.Remove(relation);
			}
			else
			{
				obj._relations.Add(relation);
			}
			touched.Add(id);
		}

		public override IEnumerable<AuthoringDiffEntry> Describe(AuthoringWorld world) =>
			[new AuthoringDiffEntry(
				added ? AuthoringDiffKind.RelationRemoved : AuthoringDiffKind.RelationAdded,
				id, null,
				added
					? $"撤销：移除关系 [{relation.RelationType}]→ {relation.TargetId}"
					: $"撤销：恢复关系 [{relation.RelationType}]→ {relation.TargetId}")];
	}

	/// <summary>逆"设置原型"：恢复旧原型引用与当时的 override 集合（清除原型会事务化清空 overrides）。</summary>
	private sealed class PrototypeEntry : UndoEntry
	{
		private readonly StableId _id;
		private readonly StableId? _oldPrototype;
		private readonly List<string> _oldOverrides;

		public PrototypeEntry(StableId id, StableId? oldPrototype, IReadOnlyCollection<string> oldOverrides)
		{
			_id = id;
			_oldPrototype = oldPrototype;
			_oldOverrides = new List<string>(oldOverrides);
		}

		public override void Restore(AuthoringWorld world, HashSet<StableId> touched)
		{
			var obj = world.Require(_id);
			obj.PrototypeId = _oldPrototype;
			obj._overrides.Clear();
			obj._overrides.UnionWith(_oldOverrides);
			touched.Add(_id);
		}

		public override IEnumerable<AuthoringDiffEntry> Describe(AuthoringWorld world) =>
			[new AuthoringDiffEntry(AuthoringDiffKind.PrototypeChanged, _id, null,
				_oldPrototype is null ? "撤销：清除原型（含覆盖记录还原）" : $"撤销：原型恢复为 {_oldPrototype}")];
	}

	/// <summary>一次已提交事务的完整历史（规范化 ops + 逆数据 + 当时 diff）。</summary>
	internal sealed class AppliedTransaction
	{
		/// <summary>事务开始时的 Id 计数器快照：失败回滚与 Undo 都要恢复它（hash 含计数器）。</summary>
		public ulong NextIdBefore { get; set; }
		public ulong NextIdAfter { get; set; }

		/// <summary>规范化 ops：自动分配的 Id 已落实为显式 Id——Redo 重放完全确定。</summary>
		public List<AuthoringOp> NormalizedOps { get; } = new();

		internal List<UndoEntry> UndoEntries { get; } = new();

		internal AuthoringDiff Diff { get; set; } = AuthoringDiff.Empty;
	}
}
