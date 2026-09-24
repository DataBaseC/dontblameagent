// 写作扩展工具包 —— 脚本插件示例。
//
// 它演示三件事：
//   ① 一个插件可以注册多个工具；
//   ② 不声明 capabilities 也能干实事（纯计算：转换 / 统计 / 抽取）；
//   ③ activate 里一行清理代码都不写 —— 卸载时宿主自动逆序撤销。
//
// 想让它读文件 / 写文件，就得在 plugin.json 的 capabilities 里声明，再用 ctx.callTool 借。

function activate(ctx) {
  ctx.log('writing-kit 激活：注册 3 个写作工具');

  ctx.registerTool({
    name: 'word_count',
    description: '统计一段文字的字数：中文字符数、英文词数、总字符数（不含空白）',
    parameters: {
      type: 'object',
      properties: { text: { type: 'string', description: '要统计的文字' } },
      required: ['text']
    },
    run: function (args) {
      var text = String(args.text || '');
      var chinese = (text.match(/[\u4e00-\u9fa5]/g) || []).length;
      var words = (text.match(/[A-Za-z0-9]+/g) || []).length;
      var chars = text.replace(/\s/g, '').length;
      return '中文 ' + chinese + ' 字 · 英文词 ' + words + ' 个 · 总字符 ' + chars + '（不含空白）';
    }
  });

  ctx.registerTool({
    name: 'paragraph_report',
    description: '段落体检：段落数、最长段字数、平均句长，并指出最该拆的那一段',
    parameters: {
      type: 'object',
      properties: { text: { type: 'string', description: '要体检的正文' } },
      required: ['text']
    },
    run: function (args) {
      var paragraphs = String(args.text || '')
        .split(/\n\s*\n/)
        .map(function (p) { return p.trim(); })
        .filter(function (p) { return p.length > 0; });

      if (paragraphs.length === 0) {
        return { success: false, error: '没读到任何段落（空文本？）' };
      }

      var longest = 0;
      var longestAt = 0;
      var totalSentences = 0;
      var totalChars = 0;

      for (var i = 0; i < paragraphs.length; i++) {
        var len = paragraphs[i].replace(/\s/g, '').length;
        if (len > longest) { longest = len; longestAt = i + 1; }
        totalChars += len;
        totalSentences += paragraphs[i].split(/[。！？!?；;]+/).filter(function (s) { return s.trim().length > 0; }).length;
      }

      var avgSentence = totalSentences === 0 ? 0 : Math.round(totalChars / totalSentences);
      var advice = longest > 200
        ? '第 ' + longestAt + ' 段偏长（' + longest + ' 字），建议拆开'
        : '段落长度还算克制';

      return '段落 ' + paragraphs.length + ' 个 · 最长 ' + longest + ' 字（第 ' + longestAt + ' 段）'
        + ' · 平均句长 ' + avgSentence + ' 字 · ' + advice;
    }
  });

  ctx.registerTool({
    name: 'outline',
    description: '把长文抽成骨架：每个段落取首句，拼成提纲草稿',
    parameters: {
      type: 'object',
      properties: {
        text: { type: 'string', description: '要抽骨架的正文' },
        limit: { type: 'string', description: '最多几条，默认 12' }
      },
      required: ['text']
    },
    run: function (args) {
      var limit = parseInt(String(args.limit || '12'), 10);
      if (!(limit > 0)) { limit = 12; }

      var paragraphs = String(args.text || '')
        .split(/\n\s*\n/)
        .map(function (p) { return p.trim(); })
        .filter(function (p) { return p.length > 0; });

      var lines = [];
      for (var i = 0; i < paragraphs.length && lines.length < limit; i++) {
        var firstSentence = paragraphs[i].split(/[。！？!?]/)[0].trim();
        if (firstSentence.length > 0) {
          lines.push('- ' + firstSentence.slice(0, 60));
        }
      }

      return lines.length === 0 ? '（没抽出内容）' : lines.join('\n');
    }
  });
}

// 装载时会自动跑一次。抛异常或返回 false = 自测不过 = 拒绝装载。
function selftest() {
  return 'ok';
}
