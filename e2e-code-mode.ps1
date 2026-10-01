# 编程模式端到端验收脚本（需要网络，必须在非受限环境运行）
# 步骤：探测端点 -> 写 agent.json（编程模式）-> 一次性跑编码任务 -> 汇总事件证据
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$base = 'https://developer.amd.com.cn/radeon/api/v1'
$key = 'rc-436902fdc3644e70ce4fb320028a351b9a7b757832e44368'
$auth = @{ Authorization = "Bearer $key" }

Write-Host '===== [1/5] pull model list ====='
$models = Invoke-RestMethod "$base/models" -Headers $auth -TimeoutSec 120
$ids = @($models.data | ForEach-Object { $_.id })
$ids | Set-Content e2e-models.txt
$ids | ForEach-Object { Write-Host "  model: $_" }
$model = $ids | Where-Object { $_ -match 'qwen|coder|deepseek|glm' } | Select-Object -First 1
if (-not $model) { $model = $ids[0] }
Write-Host "chosen model: $model"

Write-Host '===== [2/5] probe: minimal completion + minimal tool call ====='
$probeBody = @{ model = $model; messages = @(@{ role = 'user'; content = 'reply with one word: ok' }); max_tokens = 200 } | ConvertTo-Json -Depth 6
$probe = Invoke-RestMethod "$base/chat/completions" -Headers $auth -Method Post -ContentType 'application/json' -Body $probeBody -TimeoutSec 600
Write-Host "text completion: $($probe.choices[0].message.content)"

$toolFn = @{ name = 'echo'; description = 'echo text'; parameters = @{ type = 'object'; properties = @{ text = @{ type = 'string' } } } }
$toolDef = @{ type = 'function'; function = $toolFn }
$toolBody = @{ model = $model; messages = @(@{ role = 'user'; content = 'call the echo tool with text hello' }); tools = @($toolDef); max_tokens = 200 } | ConvertTo-Json -Depth 8
$toolProbe = Invoke-RestMethod "$base/chat/completions" -Headers $auth -Method Post -ContentType 'application/json' -Body $toolBody -TimeoutSec 600
$tc = $toolProbe.choices[0].message.tool_calls
if ($tc) { Write-Host "tool call: OK -> $($tc[0].function.name) $($tc[0].function.arguments)" } else { Write-Host "tool call: none (finish=$($toolProbe.choices[0].finish_reason))" }

Write-Host '===== [3/5] write agent.json (code mode) ====='
@{ cloud = @{ baseUrl = $base; apiKey = $key; model = $model }; workspace = 'agent-workspace'; sessions = 'agent-sessions'; mode = 'code'; maxSteps = 60; context = @{ tokenBudget = 24000; compressionTriggerRatio = 0.8 } } | ConvertTo-Json -Depth 6 | Set-Content agent.json -Encoding UTF8

Write-Host '===== [4/5] one-shot coding task ====='
$task = @'
coding-mode acceptance task (use tools throughout, do not answer with text only):
1) create fizzbuzz.py in the workspace: define fizzbuzz(n) returning the FizzBuzz string list for 1..n, and print results for 1..15 under __main__;
2) run it with run_command using python (or py / python3 if python is missing);
3) check the output (multiples of 3 -> Fizz, 5 -> Buzz, 15 -> FizzBuzz); if wrong, fix and rerun until correct;
4) finally report briefly: what you did, file path, run output.
'@
$runOut = dotnet run --project src/AgentFramework.Host -c Debug --no-build -- --allow-command --no-open --prompt $task 2>&1
$runOut | Set-Content e2e-run.txt
$runOut | Select-Object -Last 12 | ForEach-Object { Write-Host $_ }

Write-Host '===== [5/5] event evidence ====='
$log = Get-ChildItem agent-sessions\*.jsonl | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host "session log: $($log.FullName)"
$events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }
$tools = $events | Where-Object { $_.type -eq 'tool-call-completed' }
Write-Host "tool calls: $($tools.Count)"
$tools | ForEach-Object { Write-Host ("  [{0}] {1} ok={2}" -f $_.seq, $_.toolName, $_.success) }
$compactions = $events | Where-Object { $_.type -eq 'context-compacted' }
Write-Host "compactions: $($compactions.Count)"
$compactions | ForEach-Object { Write-Host ("  seq={0} trigger={1} {2}->{3} masked={4}" -f $_.seq, $_.trigger, $_.preTokens, $_.postTokens, $_.maskedCount) }
$usage = $events | Where-Object { $_.type -eq 'model-usage' }
Write-Host "model steps: $($usage.Count)"
Write-Host 'e2e done.'
