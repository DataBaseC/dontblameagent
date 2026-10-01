# 压缩机制加压验收（需要网络，必须在非受限环境运行）
# 小 token 预算逼出提前摘要与自动压缩，随后考「压缩后还记得什么」
$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$base = 'https://developer.amd.com.cn/radeon/api/v1'
$key = 'rc-436902fdc3644e70ce4fb320028a351b9a7b757832e44368'

# 小预算：3000 估算 token，0.8 触发压缩，0.45 提前摘要
@{ cloud = @{ baseUrl = $base; apiKey = $key; model = 'DeepSeek-V4.1-Flash' }; workspace = 'agent-workspace'; sessions = 'agent-sessions'; mode = 'code'; maxSteps = 60; context = @{ tokenBudget = 3000; compressionTriggerRatio = 0.8; earlySummarizeRatio = 0.45 } } | ConvertTo-Json -Depth 6 | Set-Content agent.json -Encoding UTF8

$prompts = @(
    '创建 multable.py：打印九九乘法表（9 行），用 run_command 运行它并确认输出正确。',
    '创建 wordstat.py：定义一段至少 60 个英文单词的文本，统计词频前 5 名并打印，用 run_command 运行确认。',
    '创建 matop.py：定义 3x3 矩阵并打印它的转置与每行求和，用 run_command 运行确认。',
    '创建 recap.py：打印一行你自己起的项目代号和当前阶段编号（自拟即可），用 run_command 运行确认。',
    '不用写新文件，直接回答：我最早让你写的脚本叫什么名字？它打印的九九乘法表里，第 7 行是什么？第二个脚本统计出的词频第 1 名是哪个词？'
)

$i = 0
foreach ($p in $prompts) {
    $i++
    Write-Host "===== prompt $i / $($prompts.Count) ====="
    $out = dotnet run --project src/AgentFramework.Host -c Debug --no-build -- --allow-command --no-open --prompt $p 2>&1
    $out | Add-Content e2e-stress.txt
    $out | Select-String -Pattern '\[统计\]|\[完成\]' | Select-Object -First 2 | ForEach-Object { Write-Host $_.Line }
}

Write-Host '===== evidence ====='
$log = Get-ChildItem agent-sessions\*.jsonl | Sort-Object LastWriteTime | Select-Object -Last 1
$events = Get-Content $log.FullName | ForEach-Object { $_ | ConvertFrom-Json }
$compactions = $events | Where-Object { $_.type -eq 'context-compacted' }
Write-Host "compactions: $($compactions.Count)"
$compactions | ForEach-Object { Write-Host ("  seq={0} trigger={1} {2}->{3} masked={4} summaryLen={5} throughTurn={6}" -f $_.seq, $_.trigger, $_.preTokens, $_.postTokens, $_.maskedCount, $(if ($_.summary) { $_.summary.Length } else { 0 }), $_.summaryThroughTurn) }
$tools = $events | Where-Object { $_.type -eq 'tool-call-completed' }
Write-Host "tool calls total: $($tools.Count)"
$usage = $events | Where-Object { $_.type -eq 'model-usage' }
Write-Host "model steps total: $($usage.Count)"
Write-Host 'stress done.'
