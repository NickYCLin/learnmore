import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

test('Swift 實際方法：remote 結果、toggle、失效、中斷與有限 WAV（fake 系統依賴）', context => {
  if (spawnSync('swift', ['--version']).status !== 0) {
    context.skip('本機無 Swift；不是原生動態驗證，既有 macOS CI 執行此 harness');
    return;
  }
  const source = readFileSync(new URL('../ios/App/App/DiagnosticAudioPlugin.swift', import.meta.url), 'utf8');
  const methods = ['failure', 'generateFixture', 'play', 'pausePlayback', 'stopPlayback', 'seekPlayback', 'handleInterruption', 'remoteCommand', 'installCommands'].map(name => {
    const start = source.search(new RegExp(`    private (?:static )?func ${name}\\(`));
    assert.notEqual(start, -1, name);
    let end = source.indexOf('{', start), depth = 1;
    while (depth && ++end < source.length) {
      if (source[end] === '{') depth++;
      if (source[end] === '}') depth--;
    }
    assert.equal(depth, 0);
    return source.slice(start, end + 1);
  }).join('\n');
  const harness = readFileSync(new URL('./native-diagnostic-harness.swift', import.meta.url), 'utf8').replace('METHODS_UNDER_TEST', methods);
  const directory = mkdtempSync(join(tmpdir(), 'learnmore-native-methods-'));
  try {
    const path = join(directory, 'main.swift');
    writeFileSync(path, harness);
    const result = spawnSync('swift', [path], { encoding: 'utf8', timeout: 60000 });
    assert.equal(result.status, 0, result.stdout + result.stderr);
    assert.match(result.stdout, /native method harness passed/);
  } finally { rmSync(directory, { recursive: true, force: true }); }
});
