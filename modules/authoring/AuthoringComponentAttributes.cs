// SPDX-License-Identifier: MIT
// AuthoringComponentAttributes.cs —— W1 Authoring Schema 的声明特性（P2.4）
//
// [Component] 标注的组件 struct 由源生成器统一收集，产出 AuthoringSchemaRegistration：
// 声明即注册（与 Feature 生成器同一哲学）——无反射、可读、可单步。
// 组件类型定义只有一份：W1（Authoring）与 W2（EcsWorld）共用同一个 struct。

using System;

namespace Baize.Authoring;

/// <summary>
/// 标记一个组件 struct（Friflo IComponent）或标签 struct（Friflo ITag）参与 W1 Authoring：
/// 生成器为其产出 Schema（字段元数据 + 按名读写 + 稳定序列化），统一注册进 AuthoringSchema。
/// </summary>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class ComponentAttribute : Attribute { }
