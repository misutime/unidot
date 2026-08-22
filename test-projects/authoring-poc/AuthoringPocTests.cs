// SPDX-License-Identifier: MIT
// AuthoringPocTests.cs —— P2.4（最小 W1 Core）可执行验收
//
// 逐条对应总方案 P2.4 退出条件（门禁）：
// 1. 同一操作经 UI 和 MCP 产生相同事务与 diff        → TestUiAndMcpProduceSameTransactionAndDiff
// 2. Undo/Redo 后 Artifact hash 完全恢复             → TestUndoRedoRestoresArtifactHash
// 3. Rename/Reparent 不破坏引用                      → TestRenameReparentKeepReferences
// 4. 单组件修改只重烘相关对象                        → TestIncrementalBakeOnlyRebakesDirty
// 5. Prefab override 可查询可解释                    → TestPrefabOverrideExplainable
// 6. 删除所有表现 Node 后模拟仍通过（纯 .NET 版）    → TestW1SceneBakedIntoW2PlaysShooter
// 另覆盖：Schema 注册/按名读写、结构化查询、确定性持久化、事务原子性。

using System;
using System.IO;
using System.Linq;

namespace AuthoringPoc.Tests;

internal static class AuthoringPocTests
{
	private static int _failures;

	public static int RunAll()
	{
		_failures = 0;

		SchemaTests.Run(Check);
		TransactionTests.RunUiAndMcpProduceSameTransactionAndDiff(Check);
		TransactionTests.RunAtomicity(Check);
		TransactionTests.RunUndoRedoRestoresArtifactHash(Check);
		ReferenceTests.RunRenameReparentKeepReferences(Check);
		QueryTests.RunStructuredQuery(Check);
		BakerTests.RunIncrementalBakeOnlyRebakesDirty(Check);
		PrefabTests.RunPrefabOverrideExplainable(Check);
		PersistenceTests.RunRoundTripIsByteStable(Check);
		EndToEndTests.RunW1SceneBakedIntoW2PlaysShooter(Check);
		TransactionTests.RunAutoIdCounterIsTransactional(Check);
		TransactionTests.RunCanonicalizeMergesEquivalentJson(Check);
		TransactionTests.RunDeleteRejectsExternalReferences(Check);
		TransactionTests.RunPrototypeCycleRejected(Check);
		PersistenceTests.RunLoadBaselineAndEdgeCases(Check);
		PersistenceTests.RunQueryJsonDeserialization(Check);
		Console.WriteLine($"authoring-poc: 测试完成, failures={_failures}");
		if (_failures != 0) return 1;

		Console.WriteLine("authoring-poc: 验证成功——P2.4 最小 W1 Core 全部退出条件通过");
		return 0;
	}

	internal static void Check(bool condition, string message)
	{
		if (condition) return;
		_failures++;
		Console.WriteLine($"authoring-poc: [FAIL] {message}");
	}
}
