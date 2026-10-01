# Goal 终止验证器端到端实测（需要网络）
# 设计：用户请求只让跑脚本；goal 额外要求写 RESULT.md ——
# 模型若跑完脚本就想收尾，验证者应判 NOT_MET 并给出差距，模型补写 RESULT.md 后才收尾。
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$base = 'https://developer.amd.com.cn/radeon/api/v1'
$key = 'rc-436902fdc3644e70ce4fb320028a351b9a7b757832e44368'

@{ cloud = @{ baseUrl = $base; apiKey = $key; model = 'DeepSeek-V4.1-Flash' }; workspace = 'agent-workspace'; sessions = 'agent-sessions'; mode = 'code'; maxSteps = 60; goal = 'add.py 运行输出 5，并且工作区存在 RESULT.md 文件，里面记录了运行结论（含数字 5）' } | ConvertTo-Json -Depth 6 | Set-Content agent.json -Encoding UTF8

Write-Host '===== goal-driven coding task ====='
$task = @'
请完成：创建 add.py，定义 add(a, b) 返回两数之和，并在 __main__ 里打印 add(2, 3) 的结果；用 run_command 运行它确认输出正确。
'@
$out = dotnet run --project src/AgentFramework.Host -c Debug --no-build -- --allow-command --no-open --prompt $task 2>&1
$out | Add-Content e2e-goal.txt
($out -split "`n" | Select-String -Pattern '\[统计\]|\[goal\]|\[完成\]') | ForEach-Object { Write-Host $_.Line.Trim() }
Write-Host '--- final answer tail ---'
($out -split "`n") | Select-Object -Last 6 | ForEach-Object { Write-Host $_ }

Write-Host '===== evidence ====='
if (Test-Path 'agent-workspace/RESULT.md') {
    Write-Host 'RESULT.md EXISTS:'
    Get-Content 'agent-workspace/RESULT.md' | Select-Object -First 5 | ForEach-Object { Write-Host "  $_" }
} else { Write-Host 'RESULT.md MISSING' }
if (Test-Path 'agent-workspace/add.py') { Write-Host 'add.py EXISTS' }

$log = Get-ChildItem agent-sessions\*.jsonl | Sort-Object LastWriteTime | Select-Object -Last 1
$events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }
$asst = $events | Where-Object { $_.type -eq 'assistant-message' }
Write-Host "assistant messages: $($asst.Count)"
$asst | ForEach-Object { $t = $_.text; if ($t.Length -gt 90) { $t = $t.Substring(0, 90) + '...' }; Write-Host "  [seq=$($_.seq)] $t" }
$tools = $events | Where-Object { $_.type -eq 'tool-call-completed' }
Write-Host "tool calls: $($tools.Count)"
Write-Host 'goal e2e done.'
