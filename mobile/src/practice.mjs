export function activeLineIndex(lines, time) {
  let result = -1;
  for (let i = 0; i < lines.length; i++) {
    if (lines[i].time > time + 0.03) break;
    result = i;
  }
  return result;
}

export function loopEnd(lines, index, duration) {
  const start = lines[index]?.time;
  if (!Number.isFinite(start)) return null;
  const next = lines.slice(index + 1).find(line => line.time > start + 0.1)?.time;
  return next ?? (Number.isFinite(duration) && duration > start ? duration : null);
}
