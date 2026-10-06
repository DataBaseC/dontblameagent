using System.Runtime.CompilerServices;

// 验证工程要直接断言容器后端的参数拼装（BuildWrapper）——禁网 / 只读根 / 只挂工作区
// 这些安全属性就写在那里，不该为了「能测」把它升级成 public API。
[assembly: InternalsVisibleTo("AgentFramework.VerifySandbox")]
