# v3.30 · 文件族工具统一口径（`read_file` / `write_file` / `grep_files` / `find_files` / 路径操作）

> v3.28 修的是命令类（`run_command`），v3.29 推广到 `shell` / `job` / `edit_file`。
> 这一版把同一条规矩落到**文件族**：失败不许只报「错在哪」，必须给「下一步怎么走」。

---

## 一、单一来源：`FileHints`

```
src/AgentFramework.Tools/FileHints.cs
```

与 `CommandHints` 同一精神、同一位置（`AgentFramework.Tools`，internal static）。
文案集中一处 —— 分散在各个工具里必然走样。对外：

| 成员 | 用途 |
|---|---|
| `PathShape` | 「（相对工作区根的路径，如 src/main.cs；绝对路径也认）」—— 漏参数时顺手告诉它该长什么样 |
| `NotFound(path)` | 文件不存在 → 「用 find_files 按名字找（如 `pattern=**/*main.cs`），或用 list_dir 看该目录有什么」 |
| `DirMissing(path)` | 目录不存在 → 看上一级 / 定位 |
| `SourceMissing(path)` | 源路径不存在 → 「路径可能已变 —— 用 list_dir 看当前目录，或 find_files 按名字找」 |
| `OutsideWrite` / `OutsideRead` | 越出工作区（写拒绝 / 读被配置收回）各自的出路 |
| `RegexHint` | 正则无效 → 「不去掉 regex=true，默认按字面量搜，括号、加号、斜杠都无需转义」 |
| `TooLongToWrite(actual, max)` | 内容过长 → 「分两次写：write_file 先写前半，再用 edit_file 补后面」 |

> 为什么值得集中：`NotFound` 那一条要用到文件名（拼 glob 示例），
> 各工具手里都有 path —— 但**怎么写示例**是同一个决定，只能有一处。

## 二、逐条改动

| 工具 | 改前 | 改后 |
|---|---|---|
| `read_file` | `缺少参数 path` | + 路径形状示例 |
| `read_file` | `文件不存在：x.md` | + 「用 find_files 找 / 用 list_dir 看」 |
| `write_file` | `缺少参数 path` / `缺少参数 content` | + 形状示例（content 补「建空文件传 ""」） |
| `write_file` | `内容过长：1000001 > 1000000` | + 「分两次写：write_file + edit_file」 |
| `write_file`（越界） | `路径越出工作区…已拒绝：x` | + 「改用工作区内的相对路径」 |
| `list_dir` | `目录不存在：x` | + 「用 list_dir 传 "." 看工作区根」 |
| `read_lines` | `缺少参数 path` / `文件不存在` | + 形状示例 / 找法 |
| `grep_files` | `缺少参数 pattern` | + 「要检索的字面量或正则，如 "TODO"」 |
| `grep_files` | `正则表达式无效：…` | + 「不用正则就去掉 regex=true」 |
| `grep_files` | `没有可扫描的文件` | + 「确认 path 是目录、include 没写太窄」 |
| `find_files` | `缺少参数 pattern` | + 「文件名 glob，如 *.cs、**/*.json」 |
| `find_files` | `起点是文件，需要目录` / `目录不存在` | + 「查文件本身用 grep_files / read_file」「用 list_dir 看上一级」 |
| `find_files` | 无匹配（Ok） | + `**` 的用法说明 |
| `make_dir` | `缺少参数 path` / `该路径上已有一个文件` | + 形状示例 / 「换个目录名，或先 move_path/delete_path」 |
| `move_path` | `缺少参数 from 或 to` / `源路径不存在` | + 形状示例 / 「路径可能已变，用 list_dir 或 find_files」 |
| `delete_path` | `缺少参数 path` / `路径不存在` | + 形状示例 / 同上的找法 |

**没动**的（本来就给了出路，保持不动）：`目标已存在（overwrite=true）`、
`目录非空（recursive=true）`、`不能删除工作区根目录本身`。

## 三、验证

`VerifyTools` 73 → **81**（+8，全绿）：

```
★ read_file 文件不存在时给出找法（find_files / list_dir）
★ write_file 内容过长时给出分段写的出路
★ grep_files 正则无效时提示「去掉 regex」
★ find_files 起点不存在时给出路
★ move_path 源不存在时给出路
★ delete_path 路径不存在时给出路
★ list_dir 目录不存在时给出路
★ read_lines 文件不存在时给出路
```

全量：**20 套 / 1321 项 / 0 失败**（v3.29 基线 1313 + 本轮 8）。

## 四、顺手记一笔

`max_results` 这类「截断」提示（grep 打满上限、find 打满上限）此前已给了出路
（「缩小 path/include 或调大 max_results」），本轮未重复改 —— 它本来就在同一口径上。
