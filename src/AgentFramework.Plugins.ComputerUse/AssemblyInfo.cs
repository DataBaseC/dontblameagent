using System.Runtime.CompilerServices;

// 验证工程要直接驱动工具与驱动（它们的参数校验 / 失败路径就写在那里），
// 不值得为了「能测」把它们升级成 public —— 插件内部类型保持 internal。
[assembly: InternalsVisibleTo("AgentFramework.VerifyComputerUse")]
