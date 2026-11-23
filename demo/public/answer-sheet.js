const sans = '"Liberation Sans", Arial, sans-serif';
const serif = '"Liberation Serif", Georgia, serif';
const handwriting = 'italic 56px "Z003", "URW Chancery L", Georgia, serif';

function wrap(context, text, width) {
  const lines = [];
  let line = '';
  for (const word of text.trim().split(/\s+/u)) {
    const joined = line ? line + ' ' + word : word;
    if (line && context.measureText(joined).width > width) {
      lines.push(line);
      line = word;
    } else {
      line = joined;
    }
  }
  if (line) lines.push(line);
  return lines;
}

export function drawAnswerSheet(canvas, qr, { teamName, blankNumber, taskNumber, statement = '', answer = '' }) {
  const left = 88;
  const right = 812;
  const width = right - left;
  canvas.width = 900;
  const context = canvas.getContext('2d');
  context.font = '35px ' + serif;
  const lines = wrap(context, statement, width);
  const solution = 572 + 44 * Math.max(0, lines.length - 1);
  canvas.height = solution + 664;

  function text(value, x, y, font, color = '#20262c', maxWidth = width) {
    context.fillStyle = color;
    context.font = font;
    context.fillText(value, x, y, maxWidth);
  }

  context.fillStyle = '#e9e7e0';
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.save();
  context.shadowColor = '#978f8180';
  context.shadowBlur = 18;
  context.shadowOffsetY = 8;
  context.fillStyle = '#fffefa';
  context.fillRect(27, 23, 846, canvas.height - 52);
  context.restore();

  text('Математическая олимпиада', left, 119, 'bold 25px ' + sans, '#414c54', 450);
  text('Бланк ответа', left, 191, 'bold 64px ' + serif, '#20262c', 460);
  text('КОМАНДА', left, 252, 'bold 20px ' + sans, '#56616a');
  text(teamName?.replace(/^Команда\s+/u, '') || '—', left, 293, '34px ' + sans, '#20262c', 445);
  text('Бланк № ' + (blankNumber ?? '—'), left, 337, '25px ' + sans);

  // The QR endpoint draws six pixels per module. Half size keeps each module crisp.
  const qrSize = qr.width / 2;
  const qrTop = Math.round(202 - qrSize / 2);
  context.imageSmoothingEnabled = false;
  context.drawImage(qr, right - qrSize, qrTop, qrSize, qrSize);
  context.imageSmoothingEnabled = true;
  context.textAlign = 'center';
  text('QR бланка', right - qrSize / 2, qrTop + qrSize + 24, '19px ' + sans, '#56616a');
  context.textAlign = 'start';
  context.fillStyle = '#343b41';
  context.fillRect(left, 381, width, 2);

  text('Задача № ' + (taskNumber ?? '—'), left, 444, 'bold 40px ' + serif);
  lines.forEach((line, i) => text(line, left, 503 + i * 44, '35px ' + serif));

  text('Решение', left, solution, 'bold 25px ' + sans);
  context.fillStyle = '#c1c6c7';
  for (let i = 1; i <= 5; i++) context.fillRect(left, solution + 71 * i, width, 1);
  const expression = /^Вычислите (\d+ \+ \d+)\.?$/u.exec(statement)?.[1];
  if (expression && answer) {
    context.save();
    context.translate(left + 23, solution + 61);
    context.rotate(-0.025);
    text(expression + ' = ' + answer, 0, 0, handwriting, '#1e437c', width - 46);
    context.restore();
  }

  const answerTop = solution + 412;
  text('Краткий ответ', left, answerTop + 61, 'bold 25px ' + sans);
  context.strokeStyle = '#737b80';
  context.lineWidth = 2;
  context.strokeRect(left + 225, answerTop, width - 225, 94);
  if (answer) {
    context.save();
    context.translate(left + 248, answerTop + 67);
    context.rotate(-0.035);
    text(answer, 0, 0, handwriting, '#1e437c', width - 274);
    context.restore();
  }
}
