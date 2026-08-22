// SPDX-License-Identifier: MIT
// Program.cs —— authoring-poc 入口（退出码 = 验收结果）

namespace AuthoringPoc;

internal static class Program
{
	public static int Main() => Tests.AuthoringPocTests.RunAll();
}
