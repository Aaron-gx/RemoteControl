using System.Runtime.CompilerServices;

// 单测要碰 internal 成员（视频丢片测试钩子等）
[assembly: InternalsVisibleTo("Agent.Common.Tests")]
