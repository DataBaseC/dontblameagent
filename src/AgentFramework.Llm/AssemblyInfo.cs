using System.Runtime.CompilerServices;

// 验收工程直接断言 checkpoint writer 的解析与裁剪：
// 「解析不出就不写」「超限时意图留到最后」这些是安全/质量属性，写在那里，
// 不该为了「能测」把它们升级成 public API。
[assembly: InternalsVisibleTo("AgentFramework.VerifyCheckpoint")]
