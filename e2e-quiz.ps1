# 压缩质量验收（需要网络，必须在非受限环境运行）
# 小预算 + 小窗口逼出提前摘要/自动压缩，然后考「压缩后还记得什么」+ 压缩后继续干活
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$base = 'https://developer.amd.com.cn/radeon/api/v1'
$key = 'rc-436902fdc3644e70ce4fb320028a351b9a7b757832e44368'

@{ cloud = @{ baseUrl = $base; apiKey = $key; model = 'DeepSeek-V4.1-Flash' }; workspace = 'agent-workspace'; sessions = 'agent-sessions'; mode = 'code'; maxSteps = 60; context = @{ tokenBudget = 2000; compressionTriggerRatio = 0.75; recentTurnsKeptVerbatim = 2; earlySummarizeRatio = 0.45 } } | ConvertTo-Json -Depth 6 | Set-Content agent.json -Encoding UTF8

Write-Host '===== quiz prompt (memory under compression) ====='
$p1 = '不用写新文件，直接回答两个问题：1) 我最早让你写的脚本叫什么名字？它打印的九九乘法表第 7 行是什么？2) 第二个脚本统计词频，第 1 名是哪个词？如果你对细节没把握，可以用 search_history 检索会话历史再回答。'
$out1 = dotnet run --project src/AgentFramework.Host -c Debug --no-build -- --allow-command --no-open --prompt $p1 2>&1
$out1 | Add-Content e2e-quiz.txt
$out1 | Select-Object -Last 10 | ForEach-Object { Write-Host $_ }

Write-Host '===== follow-up coding under compression ====='
$p2 = '再创建 finalcheck.py：打印一行 FINISH 和 1+1 的结果，用 run_command 运行确认。'
$out2 = dotnet run --project src/AgentFramework.Host -c Debug --no-build -- --allow-command --no-open --prompt $p2 2>&1
$out2 | Add-Content e2e-quiz.txt
$out2 | Select-Object -Last 10 | ForEach-Object { Write-Host $_ }

Write-Host '===== evidence ====='
$log = Get-ChildItem agent-sessions\*.jsonl | Sort-Object LastWriteTime | Select-Object -Last 1
$events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }
$c = $events | Where-Object { $_.type -eq 'context-compacted' }
Write-Host "compactions: $($c.Count)"
$c | ForEach-Object { Write-Host ("seq={0} trigger={1} {2}->{3} masked={4} summaryLen={5} throughTurn={6}" -f $_.seq, $_.trigger, $_.preTokens, $_.postTokens, $_.maskedCount, $(if ($_.summary) { $_.summary.Length } else { 0 }), $_.summaryThroughTurn) }
Write-Host 'quiz done.'
