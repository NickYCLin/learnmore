import test from 'node:test';
import assert from 'node:assert/strict';
import { activeLineIndex, loopEnd } from '../src/practice.mjs';
test('前奏不選句，重複時間戳使用最後一行', () => {
  const lines = [{time: 3}, {time: 8}, {time: 8}, {time: 15}];
  assert.equal(activeLineIndex(lines, 0), -1);
  assert.equal(activeLineIndex(lines, 8), 2);
  assert.equal(activeLineIndex(lines, 50), 3);
});
test('單句重複跳過同秒字幕，末句使用影片結尾', () => {
  const lines = [{time: 3}, {time: 3}, {time: 8}];
  assert.equal(loopEnd(lines, 0, 20), 8);
  assert.equal(loopEnd(lines, 2, 20), 20);
  assert.equal(loopEnd(lines, 2, 0), null);
  assert.equal(loopEnd([], 0, 0), null);
});
